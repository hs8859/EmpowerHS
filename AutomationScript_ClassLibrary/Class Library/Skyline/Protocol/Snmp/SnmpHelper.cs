namespace Skyline.DataMiner.Library.Protocol.Snmp
{
	using System;

	using Newtonsoft.Json;

	using Skyline.DataMiner.Library.Common.Attributes;
	using Skyline.DataMiner.Library.Protocol.Snmp.Rates;

	/// <summary>
	/// Helper class allowing to determine if an SNMP Agent has restarted.
	/// </summary>
	[DllImport("Newtonsoft.Json.dll")]
	public class SnmpHelper
	{
		private SnmpDeltaHelper deltaHelper;

		[JsonProperty]
		private double sysUptimePrevious;

		[JsonProperty]
		private TimeSpan bufferedDelta;

		private bool? isSnmpAgentRestarted = null;

		[JsonConstructor]
		private SnmpHelper(SnmpDeltaHelper deltaHelper)
		{
			this.deltaHelper = deltaHelper;
		}

		/// <summary>
		/// Deserializes a JSON <see cref="System.String"/> to a <see cref="SnmpHelper"/> instance.
		/// </summary>
		/// <param name="snmpHelperSerialized">Serialized <see cref="SnmpHelper"/> instance.</param>
		/// <param name="deltaHelper">An instance of the <see cref="SnmpDeltaHelper"/> class which will take care of fetching the SNMP Group Delta from DataMiner.</param>
		/// <returns>If the <paramref name="snmpHelperSerialized"/> is valid, a new instance of the <see cref="SnmpHelper"/> class with all data found in <paramref name="snmpHelperSerialized"/>.<br/>
		/// Otherwise, throws a <see cref="JsonReaderException"/>.</returns>
		/// <exception cref="JsonReaderException"><paramref name="snmpHelperSerialized"/> is an invalid string representation of a <see cref="SnmpHelper"/> instance.</exception>
		public static SnmpHelper FromJsonString(string snmpHelperSerialized, SnmpDeltaHelper deltaHelper)
		{
			SnmpHelper instance;
			if (!String.IsNullOrWhiteSpace(snmpHelperSerialized))
			{
				instance = JsonConvert.DeserializeObject<SnmpHelper>(snmpHelperSerialized);
				instance.deltaHelper = deltaHelper;
			}
			else
			{
				instance = new SnmpHelper(deltaHelper);
			}

			return instance;
		}

		/// <summary>
		/// Used to buffer the delta (TimeSpan) between 2 executions of the same group whenever the group execution times-out.
		/// </summary>
		/// <param name="rowKey">Optional argument to be used when buffering delta for an SNMP table.<br/>
		/// In that case, the primary key of the table row is to be provided.</param>
		public void BufferDelta(string rowKey = null)
		{
			var delta = deltaHelper.GetDelta(rowKey);
			if (!delta.HasValue)
			{
				return;
			}

			bufferedDelta += delta.Value;
		}

		/// <summary>
		/// Allows to know whether the SNMP Agent of the data source has restarted since last time we polled the sysUptime SNMP parameter.
		/// </summary>
		/// <param name="sysUptime">The last known value of the sysUptime SNMP parameter (in seconds).</param>
		/// <returns><see langword="true"/> if the data source SNMP Agent has restarted since last time sysUptime SNMP parameter was polled. Otherwise, <see langword="false"/>.</returns>
		public bool IsSnmpAgentRestarted(double sysUptime)
		{
			if (isSnmpAgentRestarted == null)
			{
				TimeSpan? delta = deltaHelper.GetDelta();

				if (sysUptime >= sysUptimePrevious || delta == null)
				{
					isSnmpAgentRestarted = false;
				}
				else
				{
					// If the previousUptime + the buffered delta + delta (in timeticks -> 100th of seconds) results in an overflow, there was a wrap around.
					// Otherwise, there was an SNMP Agent restart.
					double expectedNewSysUptimeInTimeticks = (sysUptimePrevious + (bufferedDelta + delta.Value).TotalSeconds) * 100;
					isSnmpAgentRestarted = expectedNewSysUptimeInTimeticks <= UInt32.MaxValue;
				}
			}

			bufferedDelta = TimeSpan.Zero;
			sysUptimePrevious = sysUptime;
			return (bool)isSnmpAgentRestarted;
		}

		/// <summary>
		/// Serializes the currently buffered data of this <see cref="SnmpHelper"/> instance.
		/// </summary>
		/// <returns>A JSON string containing the serialized data of this <see cref="SnmpHelper"/> instance.</returns>
		public string ToJsonString()
		{
			return JsonConvert.SerializeObject(this);
		}
	}
}