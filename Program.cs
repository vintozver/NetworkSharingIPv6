using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NetworkSharing
{
    public class ProgramConfigLanInterface
    {
        /// <summary>
        /// UUID/GUID, {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// NetworkId (the part of the address which identifies the network)
        /// </summary>
        public UInt16 NetworkId { get; }

        public ProgramConfigLanInterface(string Id, UInt16 NetworkId)
        {
            Debug.Assert(!string.IsNullOrEmpty(Id));
            this.Id = Id;
            this.NetworkId = NetworkId;
        }
    };

    public class ProgramConfig
    {
        /// <summary>
        /// LAN interface list (all interfaces served)
        /// </summary>
        public List<ProgramConfigLanInterface> LanInterfaceList = new List<ProgramConfigLanInterface>();

        /// <summary>
        /// WAN interface list (one will be chosen, by priority and availability, starting from the first)
        /// </summary>
        public List<string> WanInterfaceList = new List<string>();
    }

    static class Program
    {
        public static readonly string MyName = "NetworkSharingIPv6";
        public static readonly string MyProgramDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetworkSharingIPv6");
        private static readonly string MyProgramDir_Log = System.IO.Path.Combine(MyProgramDir, "activity.log");
        public static readonly string MyProgramDir_Config = System.IO.Path.Combine(MyProgramDir, "config.json");

        /// <summary>
        /// Write a log entry to the log file.
        /// We need this method to perform infrequent logging. We won't keep the log file open so it could be rotated.
        /// </summary>
        public static void Log(string message)
        {
            using (System.IO.StreamWriter file = new System.IO.StreamWriter(Program.MyProgramDir_Log, true))
            {
                file.WriteLine(message);
            }
        }

        /// <summary>
        /// Write a log entry to the event log. Falls back to file log if the event log is not available.
        /// </summary>
        public static void LogEventLogWarning(string message)
        {
            try
            {
                EventLog.WriteEntry(MyName, message, EventLogEntryType.Warning);
            }
            catch (InvalidOperationException)
            {
                Log(message);
            }
        }

        /// <summary>
        /// Write a log entry to the event log. Falls back to file log if the event log is not available.
        /// </summary>
        public static void LogEventLogError(string message)
        {
            try
            {
                EventLog.WriteEntry(MyName, message, EventLogEntryType.Error);
            }
            catch (InvalidOperationException)
            {
                Log(message);
            }
        }

        /// <summary>
        /// Log the list of local interfaces
        /// </summary>
        private static void LogLocalInterfaces()
        {
            using (StreamWriter log = new StreamWriter(Program.MyProgramDir_Log, true))
            {
                log.WriteLine("=== BEGIN local interfaces ===");
                foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    log.WriteLine(String.Format("Discovered interface {0}({1})", adapter.Id, adapter.Name));
                }
                log.WriteLine("=== END local interfaces ===");
                log.WriteLine("Please remember, adapters may appear and disappear, this the current state (when the service is initializing).");
            }
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main()
        {
            // Read the config file
            string JsonString;
            try
            {
                JsonString = File.ReadAllText(MyProgramDir_Config);
            }
            catch (IOException)
            {
                JsonString = "";
            }

            ProgramConfig ConfigInstance = new ProgramConfig();
            try
            {
                JToken Config = JToken.Parse(JsonString);
                JToken Config_ServedInterfaceList = Config["ServedInterfaceList"];
                foreach (JToken Config_ServedInterface_Item in Config_ServedInterfaceList)
                {
                    ConfigInstance.LanInterfaceList.Add(new ProgramConfigLanInterface(
                        Config_ServedInterface_Item["Id"].Value<string>(),
                        Config_ServedInterface_Item["NetworkId"].Value<UInt16>()
                    ));
                }
                JToken Config_WanInterfaceList = Config["WanInterfaceList"];
                foreach (JToken Config_WanInterface_Item in Config_WanInterfaceList)
                {
                    ConfigInstance.WanInterfaceList.Add(Config_WanInterface_Item["Id"].Value<string>());
                }
            }
            catch (JsonReaderException)
            {
            }
            catch (ArgumentException)
            {
            }

            LogLocalInterfaces();

            // Run services
            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new Service(ConfigInstance)
            };
            ServiceBase.Run(ServicesToRun);
        }
    }
}
