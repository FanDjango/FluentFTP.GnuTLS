﻿using System.Linq;
using System.Runtime.CompilerServices;

namespace FluentFTP.GnuTLS.Core {
	internal class GnuUtils {

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static string GetCurrentMethod([CallerMemberName] string memberName = "") {
			return "*" + memberName + "(...)";
		}

		public static int Check(string methodName, int result, params int[] resultsAllowed) {

			if (result >= 0) {
				return result;
			}

			if (resultsAllowed.Contains(result)) {
				return result;
			}

			// Consider also checking GnuTls.GnuTlsErrorIsFatal(result)

			GnuTlsException ex;

			string errTxt = GnuTlsErrorText(result);

			ex = new GnuTlsException("Error   : " + methodName + " failed: (" + result + ") " + errTxt);
			ex.ExMethod = methodName;
			ex.ExResult = result;
			ex.ExMeaning = errTxt;

			Logging.LogNoQueue(ex.Message);

			Logging.LogNoQueue("Debug   : Last " + Logging.logQueueMaxSize + " GnuTLS buffered debug messages follow:");

			foreach (string s in Logging.logQueue) {
				Logging.LogNoQueue("Debug   : " + s);
			}

			throw ex;
		}

		public static string GnuTlsErrorText(int errorCode) {
			if (!EC.ec.TryGetValue(errorCode, out string errText)) errText = "Unknown error";
			return errText;
		}

		public static bool NeedRdWrRepeat(int rslt) {
			return rslt == (int)EC.en.GNUTLS_E_AGAIN ||
				   rslt == (int)EC.en.GNUTLS_E_INTERRUPTED ||
				   rslt == (int)EC.en.GNUTLS_E_WARNING_ALERT_RECEIVED ||
				   rslt == (int)EC.en.GNUTLS_E_FATAL_ALERT_RECEIVED;
		}

	}
}