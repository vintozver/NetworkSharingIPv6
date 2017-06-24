using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Management.Automation;

namespace NetworkSharing
{
    public class AddressListener : IDisposable
    {
        private string UsedInterfaceId = string.Empty;
        private string UsedInterfaceName = string.Empty;
        private System.Net.IPAddress UsedIpAddress = null;
        private List<Process> DhcpServiceProcessList = new List<Process>();

        private void LogLocalInterfaces()
        {
            using (System.IO.StreamWriter log = new System.IO.StreamWriter(Program.MyProgramDir_Log, true))
            {
                log.WriteLine("=== BEGIN local interfaces ===");
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    log.WriteLine(String.Format("Discovered interface {0}({1})", adapter.Id, adapter.Name));
                }
                log.WriteLine("=== END local interfaces ===");
                log.WriteLine("Please remember, adapters may appear and disappear, this the current state (when the service is initializing).");
            }
        }

        private System.Net.IPAddress GetServedSubnet()
        {
            Debug.Assert(!string.IsNullOrEmpty(UsedInterfaceId));
            Debug.Assert(!string.IsNullOrEmpty(UsedInterfaceName));
            Debug.Assert(UsedIpAddress != null);
            Debug.Assert(UsedIpAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

            // Full length: 128 bit | 16 bytes
            // Prefix length: 64 bit, 8 bytes
            // Address is represented in the network order (as is)
            byte[] UsedIpAddressBytes = UsedIpAddress.GetAddressBytes();
            Debug.Assert(UsedIpAddressBytes.Length == 16);
            // We will slice this /64 subnet by /96 subnets (by 16 bits)
            // NNNN:NNNN:NNNN:NNNN:FFFF:FFFF:****:****:
            int UsedIpAddressBytesIndex = 8;
            while (UsedIpAddressBytesIndex < 12) UsedIpAddressBytes[UsedIpAddressBytesIndex++] = 0xff;
            while (UsedIpAddressBytesIndex < 16) UsedIpAddressBytes[UsedIpAddressBytesIndex++] = 0x0;

            return new System.Net.IPAddress(UsedIpAddressBytes);
        }

        private int GetInterfaceIndexById(string Id)
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.Id == Id)
                {
                    var IpProperties = adapter.GetIPProperties();
                    if (IpProperties == null)
                    {
                        throw new NetworkInformationException();
                    }
                    var IpV6Properties = IpProperties.GetIPv6Properties();
                    if (IpV6Properties == null)
                    {
                        throw new NetworkInformationException();
                    }
                    return IpV6Properties.Index;
                }
            }
            throw new ArgumentException("Network adapter with the specified Id was not found");
        }

        private string RenderDibblerServerConfig(string ServedInterfaceId, string ServedInterfaceName)
        {
            return String.Format(@"
log-level 7
log-mode short

iface ""{1}"" # {0}
{{
    T1 60  # renew timout
    T2 120  # emergency rebind timeout
    preferred-lifetime 120
    valid-lifetime 300

    class
    {{
        pool {2}/96
    }}
    # Attn! https://tools.ietf.org/html/draft-ietf-mif-dhcpv6-route-option-03
    # https://www.isc.org/blogs/routing-configuration-over-dhcpv6-2/
    route {2}/96 lifetime 300  # two lines above apply here
    # Google DNS64
    option dns-server 2001:4860:4860::6464, 2001:4860:4860::64
    option domain local
}}
", ServedInterfaceId, ServedInterfaceName.Replace("\"", "\\\""), GetServedSubnet().ToString());
        }

        private void NewDibblerServerConfigProcess(string ServedInterfaceId, string ServedInterfaceName)
        {
            string InterfaceDirectory = Path.Combine(Program.MyProgramDir, "{EFC61940-A9F8-4D73-9DA4-BF3257FC0686}");
            Directory.CreateDirectory(InterfaceDirectory);  // Won't fail if the directory already exists, or will create a new one
            string DibblerServerConfigPath = Path.Combine(InterfaceDirectory, "server.conf");
            using (FileStream ConfigFile = File.OpenWrite(DibblerServerConfigPath))
            {
                Byte[] content = new UTF8Encoding(true).GetBytes(RenderDibblerServerConfig(ServedInterfaceId, ServedInterfaceName));
                ConfigFile.Write(content, 0, content.Length);
                ConfigFile.SetLength(content.Length);
            }
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Program.MyProgramDir, "dibbler-server.exe"),
                    Arguments = "run",
                    WorkingDirectory = InterfaceDirectory
                }
            };
            DhcpServiceProcessList.Add(process);
            process.Start();

            using (PowerShell PowerShellInstance = PowerShell.Create())
            {
                PowerShellInstance.AddScript(@"param([String] $prefix, [int] $iface, [String] $gw) New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $iface -AddressFamily IPv6 -NextHop $gw -Publish Yes -PolicyStore ActiveStore -Confirm:$false
");
                PowerShellInstance.AddParameter("prefix", GetServedSubnet().ToString() + "/96");
                PowerShellInstance.AddParameter("iface", GetInterfaceIndexById(ServedInterfaceId));
                PowerShellInstance.AddParameter("gw", "::");
                PowerShellInstance.Invoke();
                foreach (var PowerShellError in PowerShellInstance.Streams.Error.ReadAll())
                {
                    EventLog.WriteEntry(Program.MyName, string.Format("Error(s) occured while adding a route. More information: {0}", PowerShellError.ToString()), EventLogEntryType.Error);
                }
            }
        }

        private void KillDibblerServerConfigProcesses(string ServedInterfaceId, string ServedInterfaceName)
        {
            using (PowerShell PowerShellInstance = PowerShell.Create())
            {
                PowerShellInstance.AddScript(@"param([String] $prefix, [int] $iface, [String] $gw) Remove-NetRoute -DestinationPrefix @($prefix) -InterfaceIndex 20 -AddressFamily IPv6 -NextHop @($gw) -Publish Yes -Confirm:$false
");
                PowerShellInstance.AddParameter("prefix", GetServedSubnet().ToString() + "/96");
                PowerShellInstance.AddParameter("iface", GetInterfaceIndexById(ServedInterfaceId));
                PowerShellInstance.AddParameter("gw", "::");
                PowerShellInstance.Invoke();
                foreach (var PowerShellError in PowerShellInstance.Streams.Error.ReadAll())
                {
                    EventLog.WriteEntry(Program.MyName, string.Format("Error(s) occured while removing the route. More information: {0}", PowerShellError.ToString()), EventLogEntryType.Error);
                }
            }
            foreach (var CurrentDhcpServiceProcess in DhcpServiceProcessList)
            {
                try
                {
                    CurrentDhcpServiceProcess.Kill();
                }
                catch (System.InvalidOperationException)
                {
                    EventLog.WriteEntry(Program.MyName, "Could not kill dibbler-server (DHCPv6 daemon). This might cause problems. Please stop the service, kill the rest of lingering daemons and restart the service again.", EventLogEntryType.Warning);
                }
            }
            DhcpServiceProcessList.Clear();
        }

        public void RefreshAddresses()
        {
            string NewUsedInterfaceId = string.Empty;
            string NewUsedInterfaceName = string.Empty;
            System.Net.IPAddress NewUsedIpAddress = null;

            NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface adapter in adapters)
            {
                // Skip non-operational adapters, loopback adapters
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var IpProperties = adapter.GetIPProperties();

                foreach (var IpAddressInfo in IpProperties.UnicastAddresses)
                {
                    var IpAddress = IpAddressInfo.Address;
                    if (IpAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                        IpAddressInfo.PrefixLength == 64 &&
                        !IpAddress.IsIPv6LinkLocal && !IpAddress.IsIPv6Multicast && !IpAddress.IsIPv6SiteLocal && !IpAddress.IsIPv6Teredo
                        )
                    {
                        NewUsedInterfaceId = adapter.Id;
                        NewUsedInterfaceName = adapter.Name;
                        NewUsedIpAddress = IpAddressInfo.Address;
                        using (System.IO.StreamWriter file = new System.IO.StreamWriter(Program.MyProgramDir_Log, true))
                        {
                            file.WriteLine(String.Format("Discovered interface {0}({1}), address {2}. Will be using this address. I won't start DHCPv6 daemon on this interface, of course.",
                                NewUsedInterfaceId, NewUsedInterfaceName, NewUsedIpAddress.ToString())
                            );
                        }
                        break;  // address has been found
                    }
                }
            }

            if (NewUsedInterfaceId == UsedInterfaceId && NewUsedIpAddress == UsedIpAddress)
            {
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(Program.MyProgramDir_Log, true))
                {
                    file.WriteLine(String.Format("No changes"));
                }
            }
            else
            {
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(Program.MyProgramDir_Log, true))
                {
                    file.WriteLine(String.Format("Changes. Killing and restarting DHCPv6 servers"));
                }

                if (UsedIpAddress != null)
                {
                    // Kill all DHCPv6 servers
                    KillDibblerServerConfigProcesses("{EFC61940-A9F8-4D73-9DA4-BF3257FC0686}", "VM LAN");
                }

                UsedInterfaceId = NewUsedInterfaceId;
                UsedInterfaceName = NewUsedInterfaceName;
                UsedIpAddress = NewUsedIpAddress;

                if (UsedIpAddress != null)
                {
                    // Start new DHCPv6 servers
                    NewDibblerServerConfigProcess("{EFC61940-A9F8-4D73-9DA4-BF3257FC0686}", "VM LAN");
                }
            }
        }

        public AddressListener()
        {
            LogLocalInterfaces();
            RefreshAddresses();  // initial "change" to bootstrap the service
        }

        public void AddressChangedCallback(object sender, EventArgs e)
        {
            RefreshAddresses();
        }

        #region IDisposable Support
        private object disposeLock = new object();
        private bool disposedFlag = false;  // To detect redundant calls

        public void Dispose()
        {
            lock (disposeLock)
            {
                if (!disposedFlag)
                {
                    if (UsedIpAddress != null)
                    {
                        KillDibblerServerConfigProcesses("{EFC61940-A9F8-4D73-9DA4-BF3257FC0686}", "VM LAN");
                    }
                    UsedIpAddress = null;
                    disposedFlag = true;
                }
            }
        }
        #endregion

        ~AddressListener()
        {
            Dispose();
        }
    }

    public partial class Service : ServiceBase
    {
        private AddressListener AddressListenerInstance;
        private NetworkAddressChangedEventHandler AddressChangeHandler;

        public Service()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            AddressListenerInstance = new AddressListener();
            AddressChangeHandler = new NetworkAddressChangedEventHandler(AddressListenerInstance.AddressChangedCallback);
            NetworkChange.NetworkAddressChanged += AddressChangeHandler;
        }

        protected override void OnStop()
        {
            NetworkChange.NetworkAddressChanged -= AddressChangeHandler;
            AddressListenerInstance.Dispose();
            AddressListenerInstance = null;
        }
    }
}
