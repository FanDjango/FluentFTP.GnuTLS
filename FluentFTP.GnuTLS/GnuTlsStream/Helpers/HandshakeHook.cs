﻿using System;
using System.IO;
using FluentFTP.GnuTLS.Core;
using FluentFTP.GnuTLS.Enums;

namespace FluentFTP.GnuTLS {

	internal partial class GnuTlsInternalStream : Stream, IDisposable {

		// handshake_hook_func(gnutls_session_t session, unsigned int htype, unsigned when, unsigned int incoming, const gnutls_datum_t* msg)
		public static int HandshakeHook(IntPtr session, uint htype, uint post, uint incoming, IntPtr msg) {

			if (session == (IntPtr)0) {
				return 0;
			}

			string action;

			// incoming  post
			// ==============
			//    1       0    received
			//    1       1    processed
			//
			//    0       0    about to send
			//    0       1    sent
			//

			if (incoming == 0) {
				// send
				action = post == 0 ? "about to send" : "sent";
			}
			else {
				// receive
				action = post == 0 ? "received" : "processed";
			}

			Logging.LogGnuFunc(GnuMessage.Handshake, "Handshake " + action + " " + Enum.GetName(typeof(HandshakeDescriptionT), htype));

			// Check for certain action/htype combinations

			if (incoming != 0 && post != 0) { // receive processed") 

				//
				// TLS1.2 : If the session ticket extension is active, a session ticke may appear
				//          ProFTPd server will do this, for example
				//          One can forbid this by setting GNUTLS_NO_TICKETS_TLS12 on the init flags
				//          or by using %NO_TICKETS_TLS12 in the priority string in config
				// TLS1.3 : A session ticket appeared
				//

				if (htype == (uint)HandshakeDescriptionT.GNUTLS_HANDSHAKE_NEW_SESSION_TICKET) {
					SessionFlagsT flags = GnuTls.GnuTlsSessionGetFlags(session);
					if (flags.HasFlag(SessionFlagsT.GNUTLS_SFLAGS_SESSION_TICKET)) {
						Logging.LogGnuFunc(GnuMessage.Handshake, "Session resume: use received session ticket");
						GnuTls.GnuTlsSessionGetData2(session, out DatumT resumeDataTLS);
						GnuTls.GnuTlsSessionSetData(session, resumeDataTLS);
						GnuTls.GnuTlsFree(resumeDataTLS.ptr);
					}
				}

			}

			return 0;
		}
	}
}
