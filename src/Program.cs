using Newtonsoft.Json;
using Renci.SshNet;
using Spectre.Console;
using System.Text;
using System.Text.RegularExpressions;

namespace DeployTool
{
    internal static partial class Program
    {
        static SshClient? sshClient;
        static ScpClient? scpClient;
        static Profile? profile;
        static bool isNew = false;
        static bool status = false;
        static void Main(string[] args)
        {
            PrintTitle();

            var opt = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[seagreen3]What do you want to do?[/]?")
                        .AddChoices("Create new Profile","Load Existing Profile"));
            string msg = "";
            if (opt == "Load Existing Profile")
            {
                LoadProfile();
                msg = "No Profiles Found. ";
            }

            if (profile == null)
            {
                AnsiConsole.MarkupLine($"[seagreen3]{msg}Create New Profile[/]");
                isNew = true;
                profile = new();
                GetSshClient();
                GetAppDetails();
            }      
            InitClients();
            Response? res = null;
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Star)
                .SpinnerStyle(Style.Parse("green bold"))
                .Start("Connecting To Server", ctx =>
                {
                    res = VerifyDetails();
                    status = res.Status;
                    if (!res.Status)
                    {
                        AnsiConsole.MarkupLine("Server Connection : [red]Failed[/]");
                        AnsiConsole.MarkupLine(res.Message);
                        return;
                    }
                    AnsiConsole.MarkupLine("Server Connection : [green]Success[/]");

                    res = GetPaths(ctx);
                    status = res.Status;
                    if (!res.Status)
                    {
                        AnsiConsole.MarkupLine("Dotnet or Nginx : [red]Not Found[/]");
                        AnsiConsole.MarkupLine(res.Message);
                        return;
                    }
                    AnsiConsole.MarkupLine("Dotnet & Nginx : [green]Found[/]");
                });
            if (status && ShowCurrentConfig())
            {
                res = DeployApp();
                res ??= new(false, "App Deployment : [red]Failed[/]");
                AnsiConsole.MarkupLine(res.Message);
                if (res.Status && !string.IsNullOrWhiteSpace(profile!.App!.Domain))
                {
                    res = AddReverseProxy();
                    res ??= new(false, "Nginx Config : [red]Failed[/]");
                    AnsiConsole.MarkupLine(res.Message);
                }
            }
            if(sshClient != null)
            {
                sshClient.Disconnect();
                sshClient.Dispose();
            }
            if(scpClient != null)
            {
                scpClient.Disconnect();
                scpClient.Dispose();
            }
            AnsiConsole.MarkupLine("Press any key to close this window . . .");
            Console.Read();
        }

        static void PrintTitle()
        {
            var titleText = new FigletText("Deploy Tool").Centered().Color(Color.Aqua);
            var title = new Padder(titleText).PadBottom(2).PadTop(1);
            AnsiConsole.Write(title);
        }

        static Dictionary<string, Profile> GetProfiles()
        {
            Dictionary<string, Profile> list = [];
            var current = Directory.GetCurrentDirectory();
            foreach (var fileName in Directory.GetFiles(current))
            {
                if (!fileName.Contains(".json"))
                    continue;
                var file = new FileInfo(fileName);
                string json = File.ReadAllText(fileName);
                try
                {
                    profile = JsonConvert.DeserializeObject<Profile>(json);
                    if (profile == null || profile.Server == null || profile.App == null)
                        continue;
                    list.Add(file.Name, profile);
                }
                finally
                {
                    profile = null;
                }
            }
            return list;
        }

        static void LoadProfile()
        {
            var list = GetProfiles();
            if (list.Count > 0)
            {
                var profileName = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select [green]Profile[/]?")
                        .PageSize(10)
                        .MoreChoicesText("[grey](Move up and down to reveal more profiles)[/]")
                        .AddChoices(list.Keys));
                profile = list[profileName];
            }
        }

        static void GetSshClient()
        {
            AnsiConsole.Write(new Rule("[yellow]Server Config[/]"));
            string host = AnsiConsole.Ask<string>("Enter [green]IP[/]?");
            string user = AnsiConsole.Prompt(new TextPrompt<string>("Enter [green]User Name? [/][grey](root) [/]").AllowEmpty());
            string pass = AnsiConsole.Prompt(new TextPrompt<string>("Enter [green]password[/]?").PromptStyle("red").Secret());
            if (string.IsNullOrWhiteSpace(user))
                user = "root";
            profile!.Server = new() 
            { 
                User = user,
                Host = host,
                Password = pass
            };            
        }

        static void InitClients()
        {
            sshClient = new(profile!.Server!.Host, profile!.Server!.User, profile!.Server!.Password);
            scpClient = new(profile!.Server!.Host, profile!.Server!.User, profile!.Server!.Password);
        }

        static void GetAppDetails()
        {
            AnsiConsole.Write(new Rule("[yellow]App Details[/]"));
            string name = AnsiConsole.Ask<string>("Enter [green]Profile Name[/]?");
            string appname = AnsiConsole.Ask<string>("Enter [green]App Name[/]?");
            string dll = AnsiConsole.Ask<string>("Enter [green]App Dll Name[/]?");
            string appType = AnsiConsole.Prompt(new SelectionPrompt<string>()
                            .Title("Select [green]App Type[/]?")
                            .PageSize(3)
                            .AddChoices("Web App","Background Worker"));
            string scheme = "";
            string domain = "";
            int port = 0;
            if (appType == "Web App")
            {
                port = AnsiConsole.Ask<int>("Enter [green]Port[/][grey] (For running the  app)[/]?");
                
                scheme = AnsiConsole.Prompt(new SelectionPrompt<string>()
                                .Title("Select [green]Scheme[/]?")
                                .PageSize(3)
                                .AddChoices("http","https"));

                domain = AnsiConsole.Prompt(new TextPrompt<string>("[grey][[Optional]][/] Enter [green]Domain[/]?").AllowEmpty());
            }
            string pub = AnsiConsole.Ask<string>("Enter [green]Publish Folder Path[/]?");
            string dest = AnsiConsole.Ask<string>("Enter [green]Destination Folder Path[/]?");
            AnsiConsole.Write(new Rule());
            profile!.Name = name;
            profile!.App = new()
            {
                Name = appname,
                Domain = domain,
                Port = port,
                DesPath = dest,
                PubPath = pub,
                ServiceName = name.Replace(' ', '_').ToLower(),
                Scheme = scheme,
                DllName = dll,
                AppType = appType,
            };
        }

        static Response VerifyDetails()
        {
            string msg = "Success";
            try
            {
                if (sshClient == null)
                    return new(false, "Invalid Credentials");
                sshClient.Connect();
                if (scpClient == null)
                    return new(false, "Invalid Credentials");
                scpClient.Connect();
                return new(true,msg);
            }
            catch (Exception ex)
            {
                msg = "Error Occured while connecting to server...\n" + ex.Message;
            }
            return new(false, msg);
        }

        static Response GetPaths(StatusContext ctx)
        {
            try
            {
                ctx.Status("Verifying Dotnet Installation");
                var cmd = sshClient!.CreateCommand("which dotnet");
                cmd.Execute();
                string dotnet = cmd.Result;
                if (string.IsNullOrEmpty(dotnet))
                    return new(false, "Dotnet Installation Not Found");
                profile!.Server!.DotnetPath = dotnet.Replace("\n", "");
            }
            catch (Exception ex)
            {
                return new(false, "Error Occured while Getting Dotnet Installation Path...\n" + ex.Message);
            }
            try
            {
                ctx.Status("Verifying Nginx Installation");
                var cmd = sshClient!.CreateCommand("nginx -V 2>&1 | grep -o '\\-\\-conf-path=\\(.*conf\\)' | cut -d '=' -f2");
                cmd.Execute();
                string nginx = cmd.Result;
                if (string.IsNullOrEmpty(nginx))
                    return new(false, "Nginx Installation Not Found");
                profile!.Server!.NginxPath = nginx.Replace("nginx.conf","").Replace("\n", "");
                return new(true, "Success");
            }
            catch (Exception ex)
            {
                return new(false, "Error Occured while Getting Nginx Config Path...\n" + ex.Message);
            }
        }

        static bool ShowCurrentConfig()
        {
            // Server Config
            StringBuilder serverConfig = new();
            serverConfig.AppendLine($"IP : [blue]{sshClient!.ConnectionInfo.Host}[/]");
            serverConfig.AppendLine($"User : [blue]{sshClient!.ConnectionInfo.Username}[/]");
            serverConfig.AppendLine($"Dotnet Path : [blue]{profile!.Server!.DotnetPath}[/]");
            serverConfig.AppendLine($"Nginx Config Path : [blue]{profile!.Server!.NginxPath}[/]");
            var serverPanel = new Panel(Align.Left(new Markup(serverConfig.ToString()),VerticalAlignment.Top))
            {
                Header = new PanelHeader("[seagreen3]Server Configuration[/]")
            };
            AnsiConsole.Write(serverPanel);

            // App Config
            StringBuilder appConfig = new();
            appConfig.AppendLine($"App Name : [blue]{profile!.App!.Name}[/]");
            appConfig.AppendLine($"App Type : [blue]{profile!.App!.AppType}[/]");
            appConfig.AppendLine($"App Domain : [blue]{profile!.App!.Domain}[/]");
            appConfig.AppendLine($"Service Name : [blue]{profile!.App!.ServiceName}[/]");
            appConfig.AppendLine($"App Scheme : [blue]{profile!.App!.Scheme}[/]");
            appConfig.AppendLine($"App Port : [blue]{profile!.App!.Port}[/]");
            appConfig.AppendLine($"App Dll Name : [blue]{profile!.App!.DllName}[/]");
            appConfig.AppendLine($"App Publish Path : [blue]{profile!.App!.PubPath}[/]");
            appConfig.AppendLine($"Destination Path : [blue]{profile!.App!.DesPath}[/]");
            var appPanel = new Panel(Align.Left(new Markup(appConfig.ToString()),VerticalAlignment.Top))
            {
                Header = new PanelHeader("[seagreen3]App Configuration[/]")
            };
            AnsiConsole.Write(appPanel);
            if (isNew)
            {
                string json = JsonConvert.SerializeObject(profile, Formatting.Indented);
                File.WriteAllText($"{profile!.Name}.json", json);
            }
            return AnsiConsole.Confirm("Do you want to deploy?");
        }

        static Response? DeployApp()
        {
            Response? res = null;
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Star)
                .SpinnerStyle(Style.Parse("green bold"))
                .Start("Deploying App", ctx =>
                {
                    try
                    {
                        var publish = new DirectoryInfo(profile!.App!.PubPath);
                        if (!publish.Exists)
                        {
                            res = new(false, "Deployment : [red]Failed[/]\n Publish Folder not found");
                            return;
                        }
                        AnsiConsole.MarkupLine("Publish Folder : [green]Found[/]");
                        
                        ctx.Status("Copying Files");
                        scpClient!.Upload(publish, profile!.App!.DesPath);
                        AnsiConsole.MarkupLine("Files Copied : [green]Successfully[/]");
                        ctx.Status("Creating Service");
                        string tempService = Path.GetRandomFileName();
                        string service = profile!.App!.AppType == "Web App" ? WebServiceTemplate : WorkerServiceTemplate;
                        service = service.Replace("#APPNAME#",profile!.App!.Name);
                        service = service.Replace("#USER#",profile!.Server!.User);
                        service = service.Replace("#DOTNETPATH#", profile!.Server!.DotnetPath);
                        service = service.Replace("#DESPATH#", profile!.App!.DesPath);
                        service = service.Replace("#DLLNAME#", profile!.App!.DllName);
                        service = service.Replace("#HOST#", profile!.Server!.Host);
                        service = service.Replace("#SCHEME#", profile!.App!.Scheme);
                        service = service.Replace("#PORT#", profile!.App!.Port.ToString());
                        File.WriteAllText(tempService, service);
                        FileInfo tempFile = new(tempService);
                        scpClient.Upload(tempFile, $"/etc/systemd/system/{profile!.App!.ServiceName}.service");
                        if(!sshClient!.IsConnected)
                            sshClient!.Connect();
                        sshClient.RunCommand("systemctl daemon-reload");
                        sshClient.RunCommand($"systemctl enable {profile!.App!.ServiceName}.service");
                        sshClient.RunCommand($"systemctl start {profile!.App!.ServiceName}.service");
                        var statCmd = sshClient.RunCommand($"systemctl status {profile!.App!.ServiceName}.service");
                        Regex regex = ServiceStatusRegex();
                        var match = regex.Match(statCmd.Result);
                        var statRes1 = match.Groups[0].Value;
                        var statRes2 = match.Groups[1].Value;
                        if (statRes1 != "active")
                        {
                            res = new(false, $"Deployment : [red]Failed[/]\n Service Status : {statRes1} ({statRes2})");
                            return;
                        }
                        AnsiConsole.MarkupLine("Service Created : [green]Successfully[/]");
                        res = new(true, "App Deployed : [green]Successfully[/]");
                        
                    }
                    catch (Exception ex)
                    {
                        res = new(false, "Deployment : [red]Failed[/]\n" + ex.Message);
                    }
                        
                });
            return res;
        }

        static Response? AddReverseProxy()
        {
            Response? res = null;
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Star)
                .SpinnerStyle(Style.Parse("green bold"))
                .Start("Adding Nginx Config", ctx =>
                {
                    try
                    {
                        string temp = Path.GetRandomFileName();
                        string nginxConf = NginxTempate;
                        nginxConf = nginxConf.Replace("#DOMAIN#", profile!.App!.Domain);
                        nginxConf = nginxConf.Replace("#HOST#", profile!.Server!.Host);
                        nginxConf = nginxConf.Replace("#SCHEME#", profile!.App!.Scheme);
                        nginxConf = nginxConf.Replace("#PORT#", profile!.App!.Port.ToString());
                        File.WriteAllText(temp, nginxConf);
                        FileInfo tempFile = new(temp);
                        scpClient!.Upload(tempFile, $"{profile.Server.NginxPath}sites-enabled/{profile.App.ServiceName}");
                        var cmd = sshClient!.CreateCommand("nginx -t");
                        cmd.Execute();
                        string result = cmd.Error;
                        if(!result.Contains("successful"))
                        {
                            res = new(false, "Nginx Config : [red]Failed[/]\n Invalid Config");
                            return;
                        }
                        sshClient.RunCommand("nginx -s reload");
                        res = new(true, "Nginx Configured : [green]Successfully[/]");
                    }
                    catch (Exception ex)
                    {
                        res = new(false, "Nginx Config : [red]Failed[/]\n" + ex.Message);
                    }
                });
            return res;
        }

        const string NginxTempate = "server {\r\n\tlisten 80 ;\r\n\tlisten [::]:80;\r\n\r\n\t# SSL configuration\r\n\t#\r\n\tlisten 443 ssl;\r\n\tlisten [::]:443 ssl;\r\n\t#ssl_certificate /usr/certs/ggndevcert.crt;\r\n\t#ssl_certificate_key /usr/certs/ggndevcert.rsa;\r\n\r\n\tserver_name #DOMAIN#;\r\n\r\n\tautoindex off;\r\n\r\n\tlocation / {\r\n\t\t# First attempt to serve request as file, then\r\n\t\t# as directory, then fall back to displaying a 404.\r\n\t\t#try_files $uri $uri/ =404;\r\n\r\n\t\tproxy_pass #SCHEME#://#HOST#:#PORT#;\r\n\t\tfastcgi_buffers 16 16k;\r\n    \t\tfastcgi_buffer_size 32k;\r\n\t\tproxy_http_version 1.1;\r\n        \tproxy_set_header   Upgrade $http_upgrade;\r\n\t        proxy_set_header   Connection $connection_upgrade;\r\n\t        proxy_set_header   Host $host;\r\n        \tproxy_cache_bypass $http_upgrade;\r\n\t        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;\r\n        \tproxy_set_header   X-Forwarded-Proto $scheme;\r\n\t\t\r\n\t}\r\n}";
        const string WebServiceTemplate = "[Unit]\r\nDescription=#APPNAME#\r\nDocumentation=https://docs.microsoft.com/en-us/aspnet/core/\r\nAfter=network.target\r\n\r\n[Service]\r\nWorkingDirectory=#DESPATH#\r\nExecStart=#DOTNETPATH# #DESPATH#/#DLLNAME#.dll --urls \"#SCHEME#://#HOST#:#PORT#\"\r\nRestart=always\r\n# Restart service after 10 seconds if the dotnet service crashes\r\nRestartSec=10\r\nSyslogIdentifier=#APPNAME#\r\nUser=#USER#\r\nEnvironment=ASPNETCORE_ENVIRONMENT=Production\r\n\r\n[Install]\r\nWantedBy=multi-user.target";
        const string WorkerServiceTemplate = "[Unit]\r\nDescription=#APPNAME#\r\nDocumentation=https://docs.microsoft.com/en-us/aspnet/core/\r\nAfter=network.target\r\n\r\n[Service]\r\nWorkingDirectory=#DESPATH#\r\nExecStart=#DOTNETPATH# #DESPATH#/#DLLNAME#.dll \r\nRestart=always\r\n# Restart service after 10 seconds if the dotnet service crashes\r\nRestartSec=10\r\nSyslogIdentifier=#APPNAME#\r\nUser=#USER#\r\n\r\n[Install]\r\nWantedBy=multi-user.target";

        [GeneratedRegex("Active: (\\w*) \\(([^)]+)\\)")]
        private static partial Regex ServiceStatusRegex();
    }

    internal record Response(bool Status, string Message);
}
