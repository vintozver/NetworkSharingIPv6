using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ServiceProcess;
using System.Net.NetworkInformation;

namespace NetworkSharing
{
    public class ServedInterface
    {
        private string _Id;  // UUID/GUID from Windows, {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
        private string _Name;  // Common name (from the Network Manager)
        private int _Index;  // Index (routing tables, internals). For PowerShell operations
        private UInt16 _NetworkId;  // NetworkId (the part of the address which identifies the network)

        public string Id
        {
            get
            {
                return _Id;
            }
        }
        public string Name
        {
            get
            {
                return _Name;
            }
        }
        public int Index
        {
            get
            {
                return _Index;
            }
        }
        public UInt16 NetworkId
        {
            get
            {
                return _NetworkId;
            }
        }

        public ServedInterface(string Id, string Name, int Index, UInt16 NetworkId)
        {
            Debug.Assert(!string.IsNullOrEmpty(Id));
            this._Id = Id;
            Debug.Assert(!string.IsNullOrEmpty(Name));
            this._Name = Name;
            Debug.Assert(Index != -1);
            this._Index = Index;
            this._NetworkId = NetworkId;
        }

        public static ServedInterface CreateFromId(string Id, UInt16 NetworkId)
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.Id.Equals(Id))
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up)
                    {
                        throw new NetworkInformationException();
                    }
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
                    return new ServedInterface(Id, adapter.Name, IpV6Properties.Index, NetworkId);
                }
            }
            throw new ArgumentException("Network adapter with the specified Id was not found");
        }
    };

    public class ServiceConfig
    {
        public HashSet<ServedInterface> ServedInterfaceList = new HashSet<ServedInterface>();
    }


    public partial class Service : ServiceBase
    {
        private ProgramConfig ProgramConfigInstance;
        private ServiceImpl ServiceImplInstance;
        private NetworkAddressChangedEventHandler AddressChangeHandler;

        private void ReloadConfiguration()
        {
            Debug.Assert(ServiceImplInstance != null);
            var ServiceConfigInstance = new ServiceConfig();
            foreach (var ProgramInterfaceDefinition in ProgramConfigInstance.InterfaceList)
            {
                ServedInterface ServedInterfaceInstance = null;
                try
                {
                    ServedInterfaceInstance = ServedInterface.CreateFromId(ProgramInterfaceDefinition.Id, ProgramInterfaceDefinition.NetworkId);
                }
                catch (NetworkInformationException)
                {
                }
                catch (ArgumentException)
                {
                }
                if (ServedInterfaceInstance != null)
                {
                    ServiceConfigInstance.ServedInterfaceList.Add(ServedInterfaceInstance);
                }

            }
            ServiceImplInstance.RefreshAddresses(ServiceConfigInstance);
        }

        public void AddressChangedCallback(object sender, EventArgs e)
        {
            ReloadConfiguration();
        }

        public Service(ProgramConfig Config)
        {
            this.ProgramConfigInstance = Config;
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            Program.Log("Starting the service");
            Debug.Assert(ServiceImplInstance == null);
            ServiceImplInstance = new ServiceImpl();
            AddressChangeHandler = new NetworkAddressChangedEventHandler(AddressChangedCallback);
            NetworkChange.NetworkAddressChanged += AddressChangeHandler;
            Program.Log("Performing initial config");
            ReloadConfiguration();
            Program.Log("Service is started");
        }

        protected override void OnStop()
        {
            Program.Log("Service is stopping ...");
            Debug.Assert(ServiceImplInstance != null);
            NetworkChange.NetworkAddressChanged -= AddressChangeHandler;
            ServiceImplInstance.Dispose();
            ServiceImplInstance = null;
            Program.Log("Service is stopped");
        }
    }
}
