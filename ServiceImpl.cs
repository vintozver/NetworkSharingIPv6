using System;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Management.Automation;

namespace NetworkSharing
{
    public class ServiceImpl : IDisposable
    {
        private ServiceConfig Configuration = new ServiceConfig();
        private string UsedInterfaceId = string.Empty;
        private string UsedInterfaceName = string.Empty;
        private System.Net.IPAddress UsedIpAddress = null;
        private Process DhcpServiceProcess = null;

        private System.Net.IPAddress GetServedSubnet(UInt16 NetworkId)
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
            // We will slice this /64 subnet as by /112 subnets as follows
            // XXXX:XXXX:XXXX:XXXX:FFFF:FFFF:NNNN:****:
            // X - assigned by provider
            // N - network id
            // * - host id (will be zero in the subnet)
            int UsedIpAddressBytesIndex = 8;
            while (UsedIpAddressBytesIndex < 12) UsedIpAddressBytes[UsedIpAddressBytesIndex++] = 0xff;
            byte[] NetworkIdBytes = BitConverter.GetBytes(NetworkId);
            if (BitConverter.IsLittleEndian) Array.Reverse(NetworkIdBytes);
            Debug.Assert(NetworkIdBytes.Length == 2);
            UsedIpAddressBytes[UsedIpAddressBytesIndex++] = NetworkIdBytes[0];
            UsedIpAddressBytes[UsedIpAddressBytesIndex++] = NetworkIdBytes[1];
            while (UsedIpAddressBytesIndex < 16) UsedIpAddressBytes[UsedIpAddressBytesIndex++] = 0x0;
            return new System.Net.IPAddress(UsedIpAddressBytes);
        }

        private string RenderDibblerServerConfig()
        {
            StringBuilder ConfigBuilder = new StringBuilder();
            ConfigBuilder.Append(@"
log-level 7
log-mode short

");
            foreach (var ServedInterface in Configuration.ServedInterfaceList)
            {
                ConfigBuilder.AppendFormat(@"
iface ""{1}"" # {0}
{{
    T1 60  # renew timout
    T2 120  # emergency rebind timeout
    preferred-lifetime 120
    valid-lifetime 300

    class
    {{
        pool {2}/112
    }}
    # Google DNS64
    option dns-server 2001:4860:4860::6464, 2001:4860:4860::64
    option domain local
}}
", ServedInterface.Id, ServedInterface.Name.Replace("\"", "\\\""), GetServedSubnet(ServedInterface.NetworkId).ToString());
            }
            return ConfigBuilder.ToString();
        }

        private void NewDibblerServerProcess()
        {
            Debug.Assert(DhcpServiceProcess == null);
            // Generate the DHCPv6 server config
            string DaemonDirectory = Path.Combine(Program.MyProgramDir, "dibbler_server");
            Directory.CreateDirectory(DaemonDirectory);  // Won't fail if the directory already exists, or will create a new one
            string DibblerServerConfigPath = Path.Combine(DaemonDirectory, "server.conf");
            using (FileStream ConfigFile = File.OpenWrite(DibblerServerConfigPath))
            {
                Byte[] content = new UTF8Encoding(true).GetBytes(RenderDibblerServerConfig());
                ConfigFile.Write(content, 0, content.Length);
                ConfigFile.SetLength(content.Length);
            }
            // Run the DHCPv6 process
            DhcpServiceProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Program.MyProgramDir, "dibbler-server.exe"),
                    Arguments = "run",
                    WorkingDirectory = DaemonDirectory
                }
            };
            DhcpServiceProcess.Start();
            // Add routes
            foreach (var ServedInterface in Configuration.ServedInterfaceList)
            {
                using (PowerShell PowerShellInstance = PowerShell.Create())
                {
                    var cmd = PowerShellInstance.AddScript("param([String] $prefix, [int] $iface, [String] $gw) New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $iface -AddressFamily IPv6 -NextHop $gw -Publish Yes -PolicyStore ActiveStore -Confirm:$false");
                    PowerShellInstance.AddParameter("prefix", GetServedSubnet(ServedInterface.NetworkId).ToString() + "/112");
                    PowerShellInstance.AddParameter("iface", ServedInterface.Index);
                    PowerShellInstance.AddParameter("gw", "::");
                    PowerShellInstance.Invoke();
                    foreach (var PowerShellError in PowerShellInstance.Streams.Error.ReadAll())
                    {
                        EventLog.WriteEntry(Program.MyName, string.Format("Error(s) occured while adding a route. More information: {0}", PowerShellError.ToString()), EventLogEntryType.Error);
                    }
                }
            }
        }

        private void KillDibblerServerProcess()
        {
            Debug.Assert(DhcpServiceProcess != null);
            // Remove routes
            foreach (var ServedInterface in Configuration.ServedInterfaceList)
            {
                using (PowerShell PowerShellInstance = PowerShell.Create())
                {
                    PowerShellInstance.AddScript("param([String] $prefix, [int] $iface, [String] $gw) Remove-NetRoute -DestinationPrefix @($prefix) -InterfaceIndex $iface -AddressFamily IPv6 -NextHop @($gw) -Publish Yes -Confirm:$false");
                    PowerShellInstance.AddParameter("prefix", GetServedSubnet(ServedInterface.NetworkId).ToString() + "/112");
                    PowerShellInstance.AddParameter("iface", ServedInterface.Index);
                    PowerShellInstance.AddParameter("gw", "::");
                    PowerShellInstance.Invoke();
                    foreach (var PowerShellError in PowerShellInstance.Streams.Error.ReadAll())
                    {
                        EventLog.WriteEntry(Program.MyName, string.Format("Error(s) occured while removing the route. More information: {0}", PowerShellError.ToString()), EventLogEntryType.Error);
                    }
                }
            }
            // Kill the DHCPv6 process
            try
            {
                DhcpServiceProcess.Kill();
            }
            catch (System.InvalidOperationException)
            {
                EventLog.WriteEntry(Program.MyName, "Could not kill dibbler-server (DHCPv6 daemon). This might cause problems. Please stop the service, kill the rest of lingering daemons and restart the service again.", EventLogEntryType.Warning);
            }
            finally
            {
                DhcpServiceProcess = null;
            }
        }

        public void RefreshAddresses(ServiceConfig NewConfiguration)
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
                        Program.Log(String.Format("Discovered interface {0}({1}), address {2}. Will be using this address. I won't start DHCPv6 daemon on this interface, of course.",
                            NewUsedInterfaceId, NewUsedInterfaceName, NewUsedIpAddress.ToString()
                        ));
                        break;  // address has been found
                    }
                }
            }

            if (NewUsedInterfaceId.Equals(UsedInterfaceId) && NewUsedIpAddress.Equals(UsedIpAddress) && NewConfiguration.ServedInterfaceList.SetEquals(Configuration.ServedInterfaceList))
            {
                Program.Log("No changes");
            }
            else
            {
                Program.Log("Changes. Killing and restarting DHCPv6 servers");

                if (UsedIpAddress != null)
                {
                    // Kill all DHCPv6 servers
                    KillDibblerServerProcess();
                }

                Configuration = NewConfiguration;

                UsedInterfaceId = NewUsedInterfaceId;
                UsedInterfaceName = NewUsedInterfaceName;
                UsedIpAddress = NewUsedIpAddress;

                if (UsedIpAddress != null)
                {
                    // Start new DHCPv6 servers
                    NewDibblerServerProcess();
                }
            }
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
                        KillDibblerServerProcess();
                    }
                    UsedIpAddress = null;
                    disposedFlag = true;
                }
            }
        }
        #endregion

        ~ServiceImpl()
        {
            Dispose();
        }
    }
}
