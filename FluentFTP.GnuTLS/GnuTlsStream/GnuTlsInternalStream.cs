﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FluentFTP.GnuTLS.Core;
using FluentFTP.GnuTLS.Enums;

namespace FluentFTP.GnuTLS {

	/// <summary>
	/// Adds support for GnuTLS TLS1.2 and TLS1.3 (with session resume capability)
	/// for FluentFTP by using a .NET c# wrapper for GnuTLS.
	/// </summary>
	internal partial class GnuTlsInternalStream : Stream, IDisposable {

		// After a successful handshake, the following will be available:
		public static string ProtocolName { get; private set; } = "Unknown";
		public static string CipherSuite { get; private set; } = "None";
		public static string? AlpnProtocol { get; private set; } = null;
		public static SslProtocols SslProtocol { get; private set; } = SslProtocols.None;
		public static int MaxRecordSize { get; private set; } = 8192;

		public bool IsResumed { get; private set; } = false;
		public bool IsSessionOk { get; private set; } = false;

		// Logging call back to our user.
		public delegate void GnuStreamLogCBFunc(string message);

		//
		// These are brought in by the .ctor
		//

		// The underlying socket of the connection
		private Socket socket;

		// The desired ALPN string to be used in the handshake
		private string alpn;

		// The desired Priority string to be used in the handshake
		private string priority;

		// The expected Host name for certificate verification
		private string hostname;

		// The Handshake Timeout to be honored on handshake
		private int htimeout;

		// The Poll Timeout to use for connectivity test
		private int ptimeout;

		//
		// For our own inside use
		//

		public Logging logging;

		// GnuTLS Handshake Hook function
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		internal delegate int GnuTlsHandshakeHookFunc(IntPtr session, uint htype, uint post, uint incoming, IntPtr msg);
		internal GnuTlsHandshakeHookFunc handshakeHookFunc = HandshakeHook;

		// Keep track: Is this the first instance or a subsequent one?
		// We need to do a "Global Init" and a "Global DeInit" when the first
		// instance is born or dies.
		private bool weAreRootStream = true;
		private static bool weAreInitialized = false;

		//

		// The TLS session associated with this GnuTlsStream
		private ClientSession sess;

		// The Certificate Credentials associated with this
		// GnuTlsStream and ALL streams resumed from it
		// One for all of these, therefore static
		private static CertificateCredentials cred;

		// Storage for resume data:
		// * retrieved from the "session-to-be-resumed"
		// * used for a session that is "to-be-resumed"
		// * re-used, therefore static
		private static DatumT resumeDataTLS = new();

		// Handle for gnutls-30.dll
		public static IntPtr hDLL = IntPtr.Zero;

		//
		// Constructor
		//

		public GnuTlsInternalStream(
			string targetHostString,
			Socket socketDescriptor,
			CustomRemoteCertificateValidationCallback customRemoteCertificateValidation,
			X509CertificateCollection clientCertificates,
			string? alpnString,
			GnuTlsInternalStream streamToResume,
			string priorityString,
			int handshakeTimeout,
			int pollTimeout,
			GnuStreamLogCBFunc elog,
			int logMaxLevel,
			GnuMessage logDebugInformationMessages,
			int logQueueMaxSize) {

			socket = socketDescriptor;
			alpn = alpnString;
			priority = priorityString;
			hostname = targetHostString;
			htimeout = handshakeTimeout;
			ptimeout = pollTimeout;

			weAreRootStream = streamToResume == null;

			if (!weAreInitialized) {

				// On the first instance of GnuTlsStream, setup:
				// 1. Logging
				// 2. Make sure GnuTls version corresponds to our Native. and Enums.
				// 3. GnuTls Gobal Init
				// 4. One single credentials set

				Logging.InitLogging(elog, logMaxLevel, logDebugInformationMessages, logQueueMaxSize);

				Validate(true);

				Logging.AttachGnuTlsLogging();

				// GnuTlsStreams are organized as
				// TLS 1.2:
				// First one: Creates/Initializes the GnuTls infrastructure, cannot resume
				// Subsequent ones: Re-use part of the GnuTls infrastructure, can resume from first
				// one or previous ones
				// TLS 1.3:
				// Additionally, Session Tickets to store session data may appear at any time
				if (streamToResume != null) {
					throw new GnuTlsException("Cannot resume from anything if fresh stream");
				}

				// Setup the GnuTLS infrastructure
				GnuTls.GnuTlsGlobalInit();

				// Setup/Allocate certificate credentials for this first session
				cred = new();

				// sets the system trusted CAs for Internet PKI
				int n = GnuTls.GnuTlsCertificateSetX509SystemTrust(cred.ptr);
				if (n > 0) {
					Logging.LogGnuFunc(GnuMessage.Handshake, "Processed " + n + " certificates in system X509 trust list");
				}
				else {
					Logging.LogGnuFunc(GnuMessage.Handshake, "Loading system X509 trust list failed: " + GnuUtils.GnuTlsErrorText(n));
				}

				weAreInitialized = true;
			}

			// Any client certificates for presentation to server?
			SetupClientCertificates(clientCertificates);

			sess = new(/*InitFlagsT.GNUTLS_NO_TICKETS_TLS12*/);

			SetupHandshake();

			// Setup handshake hook
			GnuTls.GnuTlsHandshakeSetHookFunction(sess, (uint)HandshakeDescriptionT.GNUTLS_HANDSHAKE_ANY, (int)HandshakeHookT.GNUTLS_HOOK_BOTH, handshakeHookFunc);


			IsSessionOk = true;

			// Setup Session Resume
			if (streamToResume != null) {
				Logging.LogGnuFunc(GnuMessage.Handshake, "Session resume: Use session data from control connection");
				GnuTls.GnuTlsSessionGetData2(streamToResume.sess, out resumeDataTLS);
				GnuTls.GnuTlsSessionSetData(sess, resumeDataTLS);
				GnuTls.GnuTlsFree(resumeDataTLS.ptr);
			}

			DisableNagle();

			GnuTls.GnuTlsHandShake(sess);

			ReEnableNagle();

			PopulateHandshakeInfo();

			ReportClientCertificateUsed();

			ValidateServerCertificates(customRemoteCertificateValidation);

		}

		// Destructor

		~GnuTlsInternalStream() {
		}

		// Dispose

		public void Dispose() {
			if (sess != null) {
				if (IsSessionOk) {
					int count = GnuTls.GnuTlsRecordCheckPending(sess);
					if (count > 0) {
						byte[] buf = new byte[count];
						this.Read(buf, 0, count);
					}
					GnuTls.GnuTlsBye(sess, CloseRequestT.GNUTLS_SHUT_RDWR);
				}
				sess.Dispose();
			}

			if (weAreInitialized && weAreRootStream) {
				cred.Dispose();
				GnuTls.GnuTlsGlobalDeInit();
				GnuTls.FunctionLoader.Free();
				weAreInitialized = false;
			}

		}

		// Methods overriding base ( = System.IO.Stream )

		public override int Read(byte[] buffer, int offset, int maxCount) {
			if (maxCount <= 0) {
				throw new ArgumentException("GnuTlsInternalStream.Read: maxCount must be greater than zero");
			}
			if (offset + maxCount > buffer.Length) {
				throw new ArgumentException("GnuTlsInternalStream.Read: offset + maxCount go beyond buffer length");
			}

			maxCount = Math.Min(maxCount, MaxRecordSize);

			int result;
			bool needRepeat;
			int msMax;
			int repeatCount = 0;

			var stopWatch = new Stopwatch();
			stopWatch.Start();

			do {
				result = GnuTls.GnuTlsRecordRecv(sess, buffer, maxCount);

				if (result >= (int)EC.en.GNUTLS_E_SUCCESS) {
					break;
				}

				needRepeat = GnuUtils.NeedRepeat(GnuUtils.RepeatType.Read, result, out msMax);

				long msElapsed = stopWatch.ElapsedMilliseconds;

				if ((msElapsed < msMax) && needRepeat) {
					repeatCount++;

					// if (repeatCount <= 2) Logging.LogGnuFunc(GnuMessage.Read, "*GnuTlsRecordRecv(...) repeat due to " + Enum.GetName(typeof(EC.en), result));

					switch (result) {
						case (int)EC.en.GNUTLS_E_WARNING_ALERT_RECEIVED:
							Logging.LogGnuFunc(GnuMessage.Alert, "Warning alert received: " + GnuTls.GnuTlsAlertGetName(GnuTls.GnuTlsAlertGet(sess)));
							break;
						case (int)EC.en.GNUTLS_E_FATAL_ALERT_RECEIVED:
							Logging.LogGnuFunc(GnuMessage.Alert, "Fatal alert received: " + GnuTls.GnuTlsAlertGetName(GnuTls.GnuTlsAlertGet(sess)));
							break;
						default:
							break;
					}
				}
			} while (needRepeat);

			// if (repeatCount > 2) Logging.LogGnuFunc(GnuMessage.Read, "*GnuTlsRecordRecv(...) " + repeatCount + " repeats overall");

			stopWatch.Stop();

			return GnuUtils.Check("*GnuTlsRecordRecv(...)", result);

		}

		public override void Write(byte[] buffer, int offset, int count) {
			if (count <= 0) {
				throw new ArgumentException("GnuTlsInternalStream.Write: count must be greater than zero");
			}
			if (offset + count > buffer.Length) {
				throw new ArgumentException("GnuTlsInternalStream.Write: offset + count go beyond buffer length");
			}

			byte[] buf = new byte[count];

			Array.Copy(buffer, offset, buf, 0, count);

			int result = int.MaxValue;
			bool needRepeat;
			int msMax;
			int repeatCount;
			var stopWatch = new Stopwatch();

			repeatCount = 0;
			stopWatch.Start();

			while (result > 0) {

				do {
					result = GnuTls.GnuTlsRecordSend(sess, buf, Math.Min(buf.Length, MaxRecordSize));
					if (result >= (int)EC.en.GNUTLS_E_SUCCESS) {
						break;
					}

					needRepeat = GnuUtils.NeedRepeat(GnuUtils.RepeatType.Write, result, out msMax);

					long msElapsed = stopWatch.ElapsedMilliseconds;

					if ((msElapsed < msMax) && needRepeat) {
						repeatCount++;

						// if (repeatCount <= 2) Logging.LogGnuFunc(GnuMessage.Read, "*GnuTlsRecordSend(...) repeat due to " + Enum.GetName(typeof(EC.en), result));

						switch (result) {
							case (int)EC.en.GNUTLS_E_WARNING_ALERT_RECEIVED:
								Logging.LogGnuFunc(GnuMessage.Alert, "Warning alert received: " + GnuTls.GnuTlsAlertGetName(GnuTls.GnuTlsAlertGet(sess)));
								break;
							case (int)EC.en.GNUTLS_E_FATAL_ALERT_RECEIVED:
								Logging.LogGnuFunc(GnuMessage.Alert, "Fatal alert received: " + GnuTls.GnuTlsAlertGetName(GnuTls.GnuTlsAlertGet(sess)));
								break;
							default:
								break;
						}
					}
				} while (needRepeat);


				int newLength = buf.Length - result;
				if (newLength <= 0) {
					break;
				}
				Array.Copy(buf, result, buf, 0, newLength);
				Array.Resize(ref buf, buf.Length - result);
			}

			// if (repeatCount > 2) Logging.LogGnuFunc(GnuMessage.Read, "*GnuTlsRecordSend(...) " + repeatCount + " repeats overall");

			stopWatch.Stop();

			GnuUtils.Check("*GnuTlsRecordSend(...)", result);
		}

		public override bool CanRead {
			get {
				return IsSessionOk;
			}
		}

		public override bool CanWrite {
			get {
				return IsSessionOk;
			}
		}

		public override bool CanSeek { get { return false; } }

		public override long Length => throw new NotImplementedException();
		public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

		public override void Flush() {
			// Do we need to do anything here? This is actually invoked.
		}

		public override long Seek(long offset, SeekOrigin origin) {
			throw new NotImplementedException();
		}

		public override void SetLength(long value) {
			throw new NotImplementedException();
		}

		// Methods extending base ( = System.IO.Stream )

		// Make our version accessible
		public static string GetLibVersion() {
			return GnuUtils.GetLibVersion();
		}

		// Internal methods

		internal static bool Validate(bool log) {

			string gnuTlsVersionNeeded = "3.7.8";

			PlatformID platformID = Environment.OSVersion.Platform;

			string applicationVersion = GnuUtils.GetLibVersion() + "(" + platformID.ToString() + "/" + GnuUtils.GetLibTarget() + ")";

			if ((int)platformID != 2 && (int)platformID != 4 && (int)platformID != 6 && (int)platformID != 128) {
				Logging.Log("FluentFTP.GnuTLS " + applicationVersion);
				Exception nex = new GnuTlsException("Unsupported platform: " + platformID.ToString());
				throw new GnuTlsException("Environment validation error", nex);
			}

			if (!Environment.Is64BitProcess) {
				Logging.Log("FluentFTP.GnuTLS " + applicationVersion);
				Exception nex = new GnuTlsException("GnuTlsStream needs to be run as a 64bit process");
				throw new GnuTlsException("Process validation error", nex);
			}

			GnuTls.LoadAllFunctions();

			string gnuTlsVersion;

			try {
				gnuTlsVersion = GnuTls.GnuTlsCheckVersion(null);
			}
			catch (Exception ex) {
				Logging.Log("FluentFTP.GnuTLS " + applicationVersion);
				if (ex.InnerException != null) {
					Exception nex = new GnuTlsException(ex.InnerException.Message);
					throw new GnuTlsException("GnuTLS .dll load/call validation error", nex);
				}
				else {
					throw new GnuTlsException("GnuTLS .dll load/call validation error", ex);
				}
			}

			if (log) {
				Logging.Log("FluentFTP.GnuTLS " + applicationVersion + " / GnuTLS " + gnuTlsVersion);
			}

			// Under windows, we need the explicitly built libgnutls.dll, whose version we know from our build chain
			// Under linux, we ignore the version and take whatever the distro provides (and hope for the best)
			if ((int)platformID == 2 && gnuTlsVersion != gnuTlsVersionNeeded) {
				Exception nex = new GnuTlsException("GnuTLS library version must be " + gnuTlsVersionNeeded);
				throw new GnuTlsException("GnuTLS .dll version validation error", nex);
			}

			return true;
		}

	}
}
