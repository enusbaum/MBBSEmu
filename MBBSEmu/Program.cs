﻿using MBBSEmu.Database.Repositories.Account;
using MBBSEmu.Database.Repositories.AccountKey;
using MBBSEmu.DependencyInjection;
using MBBSEmu.HostProcess;
using MBBSEmu.Module;
using MBBSEmu.Reports;
using MBBSEmu.Resources;
using MBBSEmu.Telnet;
using Microsoft.Extensions.Configuration;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MBBSEmu
{
    class Program
    {
        private static readonly ILogger _logger = ServiceResolver.GetService<ILogger>();

        static void Main(string[] args)
        {
            var sInputModule = string.Empty;
            var sInputPath = string.Empty;
            var bApiReport = false;
            var bConfigFile = false;
            var sConfigFile = string.Empty;
            var bResetDatabase = false;
            var config = ServiceResolver.GetService<IConfigurationRoot>();

            if (args.Length == 0)
                args = new[] {"-?"};

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToUpper())
                {
                    case "-DBRESET":
                    {
                        bResetDatabase = true;
                        break;
                    }
                    case "-APIREPORT":
                        bApiReport = true;
                        break;
                    case "-M":
                        sInputModule = args[i + 1];
                        i++;
                        break;
                    case "-P":
                        sInputPath = args[i + 1];
                        i++;
                        break;
                    case "-?":
                        Console.WriteLine(new ResourceManager().GetString("MBBSEmu.Assets.commandLineHelp.txt"));
                        return;
                    case "-CONFIG":
                    case "-C":
                    {
                        bConfigFile = true;
                        //Is there a following argument that doesn't start with '-'
                        //If so, it's the config file name
                        if (i + 1 < args.Length && args[i + 1][0] != '-')
                        {
                            sConfigFile = args[i + 1];

                            if (!File.Exists(sConfigFile))
                            {
                                    Console.Write($"Specified Module Configuration File not found: {sConfigFile}");
                                    return;
                            }
                            i++;
                        }
                        else
                        {
                           Console.WriteLine("Please specify a Module Configuration File when using the -C command line option");
                        }

                        break;
                    }
                    default:
                        Console.WriteLine($"Unknown Command Line Argument: {args[i]}");
                        return;
                }
            }

            //Database Reset
            if (bResetDatabase)
                DatabaseReset();

            //Setup Generic Database
            if (!File.Exists("BBSGEN.DAT"))
            {
                _logger.Warn($"Unable to find MajorBBS/WG Generic User Database, creating new copy of BBSGEN.VIR to BBSGEN.DAT");
                if (!File.Exists("BBSGEN.VIR"))
                {
                    _logger.Fatal("Unable to locate BBSGEN.VIR -- aborting");
                    return;
                }

                File.Copy("BBSGEN.VIR", "BBSGEN.DAT");
            }

            //Setup Modules
            var modules = new List<MbbsModule>();
            if (!string.IsNullOrEmpty(sInputModule))
            {
                //Load Command Line
                modules.Add(new MbbsModule(sInputModule, sInputPath));
            }
            else if(bConfigFile)
            {
                //Load Config File
                var moduleConfiguration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile(sConfigFile, optional: false, reloadOnChange: true).Build();

                foreach (var m in moduleConfiguration.GetSection("Modules").GetChildren())
                {
                    _logger.Info($"Loading {m["Identifier"]}");
                    modules.Add(new MbbsModule(m["Identifier"], m["Path"]));
                }
            }
            else
            {
                _logger.Warn($"You must specify a module to load either via Command Line or Config File");
                _logger.Warn($"View help documentation using -? for more information");
                return;
            }

            //API Report
            if (bApiReport)
            {
                foreach (var m in modules)
                {
                    var apiReport = new ApiReport(m);
                    apiReport.GenerateReport();
                }
                return;
            }

            //Database Sanity Checks
            var databaseFile = ServiceResolver.GetService<IConfigurationRoot>()["Database.File"];
            if (string.IsNullOrEmpty(databaseFile))
            {
                _logger.Fatal($"Please set a valid database filename (eg: mbbsemu.db) in the appsettings.json file before running MBBSEmu");
                return;
            }
            if (!File.Exists(databaseFile))
            {
                _logger.Warn($"SQLite Database File {databaseFile} missing, performing Database Reset to perform initial configuration");
                DatabaseReset();
            }

            //Setup and Run Host
            var host = ServiceResolver.GetService<IMbbsHost>();
            host.Start();
            foreach(var m in modules)
                host.AddModule(m);

            //Setup and Run Telnet Server
            ServiceResolver.GetService<ITelnetServer>().Start();
        }

        private static void DatabaseReset()
        {
            _logger.Info("Resetting Database...");
            var acct = ServiceResolver.GetService<IAccountRepository>();
            if (acct.TableExists())
                acct.DropTable();

            acct.CreateTable();
            var sysopUserId = acct.InsertAccount("sysop", "sysop", "sysop@mbbsemu.com");
            var guestUserId = acct.InsertAccount("guest", "guest", "guest@mbbsemu.com");

            var keys = ServiceResolver.GetService<IAccountKeyRepository>();

            if (keys.TableExists())
                keys.DropTable();

            keys.CreateTable();

            keys.InsertAccountKey(sysopUserId, "DEMO");
            keys.InsertAccountKey(sysopUserId, "NORMAL");
            keys.InsertAccountKey(sysopUserId, "SUPER");
            keys.InsertAccountKey(sysopUserId, "SYSOP");

            keys.InsertAccountKey(guestUserId, "DEMO");
            keys.InsertAccountKey(guestUserId, "NORMAL");

            _logger.Info("Database Reset!");
        }
    }
}
