﻿using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NuPU
{
    /// <summary>
    /// Main entry point for updating NuGet packages.
    /// Numerous lines in this file are highly inspired (some copied) from NuKeeper (https://github.com/NuKeeperDotNet/NuKeeper)
    /// and dotnet-outdated (https://github.com/dotnet-outdated/dotnet-outdated). For much better and feature rich tools, check those out.
    /// This is private/internal tool for now. It would be worth to consider donating it to either NuKeeper or dotnet-outdated.
    /// </summary>
    public class Program
    {
        private const string UpToDate = " [green]up to date[/]";
        private const string NeedsUpdate = " [red]needs update[/]";

        public static async Task<int> Main(string[] args)
        {
            var rootPath = args != null && args.Length == 1 && Directory.Exists(args[0]) ? args[0] : Directory.GetCurrentDirectory();
            var settings = Settings.LoadDefaultSettings(rootPath);
            var enabledSources = SettingsUtility.GetEnabledSources(settings);

            var rootDir = new DirectoryInfo(rootPath);
            var csProjFiles = rootDir.EnumerateFiles("*.csproj", SearchOption.AllDirectories);
            var ignoreDirs = new[] { ".git", ".github", ".vs", ".vscode", "bin", "obj", "packages", "node_modules" };
            foreach (var csProjFile in csProjFiles.Where(f => !ignoreDirs.Contains(f.DirectoryName)))
            {
                AnsiConsole.MarkupLine($"Analyzing [yellow]{csProjFile.FullName}[/]");
                var packages = new List<Package>();
                using (var fileStream = File.OpenRead(csProjFile.FullName))
                {
                    var document = XDocument.Load(fileStream);
                    var ns = document.Root.GetDefaultNamespace();
                    var project = document.Element(ns + "Project");
                    var itemGroups = project
                        .Elements(ns + "ItemGroup")
                        .ToList();
                    packages.AddRange(itemGroups.SelectMany(ig => ig.Elements(ns + "PackageReference")).Select(e => new Package
                    {
                        Id = e.Attribute("Include")?.Value,
                        Version = e.Attribute("Version")?.Value ?? e.Element(ns + "Version")?.Value,
                    }));
                }

                if (packages.Count() == 0) continue;

                var projectFileResults = new Dictionary<string, string>();

                foreach (var package in packages)
                {
                    var nugetVersion = new NuGetVersion(package.Version);

                    AnsiConsole.Markup(package.Id);

                    foreach (var source in enabledSources)
                    {
                        var repository = new SourceRepository(source, Repository.Provider.GetCoreV3());
                        try
                        {
                            FindPackageByIdResource resource = await repository.GetResourceAsync<FindPackageByIdResource>();
                            using var cacheContext = new SourceCacheContext();
                            var allVersions = await resource.GetAllVersionsAsync(package.Id, cacheContext, NullLogger.Instance, CancellationToken.None);
                            var newerVersions = allVersions.Where(v => v > nugetVersion);
                            if (newerVersions.Count() == 0)
                            {
                                AnsiConsole.MarkupLine(UpToDate);
                                break;
                            }

                            var versionsToShow = new List<NuGetVersion>();
                            var highestNewerMajor = newerVersions.Where(v => v.Version.Major > nugetVersion.Major).GroupBy(v => v.Version.Major).Select(g => g.OrderByDescending(v => v.Version).First());
                            versionsToShow.AddRange(highestNewerMajor);
                            var highestNewerMinor = newerVersions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor > nugetVersion.Minor).OrderByDescending(v => v.Version).FirstOrDefault();
                            if (highestNewerMinor != null) versionsToShow.Add(highestNewerMinor);
                            var highestNewerPatch = newerVersions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor == nugetVersion.Minor && v.Version.Build > nugetVersion.Patch).OrderByDescending(v => v.Version).FirstOrDefault();
                            if (highestNewerPatch != null) versionsToShow.Add(highestNewerPatch);
                            var highestNewerRevision = newerVersions.Where(v => v.Version.Major == nugetVersion.Major && v.Version.Minor == nugetVersion.Minor && v.Version.Build == nugetVersion.Patch && v.Version.Revision > nugetVersion.Revision).OrderByDescending(v => v.Version).FirstOrDefault();
                            if (highestNewerRevision != null) versionsToShow.Add(highestNewerRevision);

                            if (versionsToShow.Count == 0)
                            {
                                AnsiConsole.MarkupLine(UpToDate);
                                break;
                            }

                            var choices = new List<string>();
                            var currentVersionString = $"{package.Version} (current)";
                            choices.Add(currentVersionString);
                            choices.AddRange(versionsToShow.OrderBy(v => v.Version).Select(v => v.ToString()));

                            AnsiConsole.MarkupLine(NeedsUpdate);
                            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>().PageSize(10).AddChoices(choices.ToArray()));

                            if (choice == currentVersionString) break;

                            var dotnet = new ProcessStartInfo("dotnet", $"add package {package.Id} -v {choice} -s {source.SourceUri}")
                            {
                                WorkingDirectory = csProjFile.Directory.FullName,
                                CreateNoWindow = true,
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            };
                            var process = Process.Start(dotnet);
                            var outputAndError = await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());

                            process.WaitForExit();
                            var exitCode = process.ExitCode;

                            if (exitCode < 0)
                            {
                                Console.WriteLine(outputAndError[1]);
                                return -1;
                            }

                            if (!string.IsNullOrWhiteSpace(outputAndError[0]))
                            {
                                var lines = outputAndError[0].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                                if (lines.Length > 0 && lines.Last().StartsWith("error"))
                                {
                                    AnsiConsole.MarkupLine($"[red]{lines.Last()}[/]");
                                }
                            }

                            break;
                        }
                        catch {}
                    }
                }
            }

            return 0;
        }

        private class Package
        {
            public string Id { get; set; }
            public string Version { get; set; }
        }
    }
}