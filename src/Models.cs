using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeployTool
{
    public class AppConfig
    {
        public string Name { get; set; } = "";
        public string Domain { get; set; } = "";
        public string DllName { get; set; } = "";
        public string PubPath { get; set; } = "";
        public string DesPath { get; set; } = "";
        public string AppType { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string Scheme { get; set; } = "";
        public int Port { get; set; }
    }

    public class Profile
    {
        public string Name { get; set; } = "";
        public AppConfig? App { get; set; }
        public ServerConfig? Server { get; set; }
    }

    public class ServerConfig
    {
        public string Host { get; set; } = "";
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        public string DotnetPath { get; set; } = "";
        public string NginxPath { get; set; } = "";
    }

}
