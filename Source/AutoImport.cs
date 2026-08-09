using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Playnite.SDK.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Text.RegularExpressions;
using System.Xml;

namespace AutoImportPlugin
{
    public class AutoImport : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private AutoImportSettingsViewModel settings { get; set; }

        private Process launcherProcess;
        private System.Threading.Timer launcherCheckTimer;

        private const string ReShadeDeployerPath =
            @"G:\Reshade\Reshade Deployer.exe";

        // ============================================================
        // DS4WINDOWS
        // ============================================================

        private const string DS4WindowsPath =
            @"C:\Program Files (x86)\net8.0-windows7.0.Full\DS4Windows.exe";

        private const string DS4WindowsAutoProfilesPath =
            @"C:\Program Files (x86)\net8.0-windows7.0.Full\Auto Profiles.xml";

        // Nom Windows en dur comme demandé.
        private const string WindowsUserName = "Hugo";

        // Chemin de l'exécutable du jeu pour lequel le HDR doit être activé
        private string pendingHdrExecutablePath;

        // Chemin de l'exécutable du jeu dont on attend les métadonnées
        private string pendingMetadataExecutablePath;

        // Jeux pour lesquels le lien PCGamingWiki a déjà été ouvert
        private readonly HashSet<Guid> openedPcGamingWikiGames =
            new HashSet<Guid>();

        public override Guid Id { get; } =
            Guid.Parse("92c96e54-069b-4fc4-bbaa-35ac3064f85a");

        public override string Name => "AutoImport";

        public AutoImport(IPlayniteAPI api) : base(api)
        {
            settings = new AutoImportSettingsViewModel(this);

            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };

            launcherCheckTimer = new System.Threading.Timer(
                CheckLauncherProcess,
                null,
                0,
                1000
            );
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override System.Windows.Controls.UserControl GetSettingsView(
            bool firstRunSettings)
        {
            return new AutoImportSettingsView();
        }

        public override IEnumerable<GameMetadata> GetGames(
            LibraryGetGamesArgs args)
        {
            return ScanAndSelectGames();
        }

        // ============================================================
        // SURVEILLANCE DU LAUNCHER
        // ============================================================

        private void CheckLauncherProcess(object state)
        {
            try
            {
                if (launcherProcess != null)
                {
                    if (!launcherProcess.HasExited)
                        return;

                    launcherProcess.Dispose();
                    launcherProcess = null;

                    Application.Current.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            ScanAndSelectGames();
                        })
                    );

                    return;
                }

                var processes =
                    Process.GetProcessesByName("launcher");

                if (processes.Length > 0)
                {
                    launcherProcess = processes[0];
                }

                foreach (var process in processes.Skip(1))
                {
                    process.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    "Failed to monitor launcher process"
                );
            }
        }

        // ============================================================
        // LIBRARY UPDATED
        // ============================================================

        public override void OnLibraryUpdated(
            OnLibraryUpdatedEventArgs args)
        {
            base.OnLibraryUpdated(args);

            try
            {
                // ========================================================
                // HDR
                // ========================================================

                if (!string.IsNullOrEmpty(pendingHdrExecutablePath))
                {
                    string targetPath =
                        NormalizePath(pendingHdrExecutablePath);

                    foreach (var game in PlayniteApi.Database.Games)
                    {
                        if (game.GameActions == null)
                            continue;

                        bool isTargetGame =
                            game.GameActions.Any(action =>
                                action.Type == GameActionType.File &&
                                !string.IsNullOrEmpty(action.Path) &&
                                NormalizePath(action.Path) == targetPath
                            );

                        if (!isTargetGame)
                            continue;

                        game.EnableSystemHdr = true;

                        PlayniteApi.Database.Games.Update(game);

                        logger.Info(
                            $"Enabled system HDR for imported game: {game.Name}"
                        );

                        pendingHdrExecutablePath = null;

                        break;
                    }
                }

                // ========================================================
                // PCGAMINGWIKI
                // ========================================================

                if (!string.IsNullOrEmpty(
                    pendingMetadataExecutablePath))
                {
                    string targetPath =
                        NormalizePath(
                            pendingMetadataExecutablePath
                        );

                    foreach (var game in PlayniteApi.Database.Games)
                    {
                        if (game.GameActions == null)
                            continue;

                        bool isTargetGame =
                            game.GameActions.Any(action =>
                                action.Type == GameActionType.File &&
                                !string.IsNullOrEmpty(action.Path) &&
                                NormalizePath(action.Path) == targetPath
                            );

                        if (!isTargetGame)
                            continue;

                        if (openedPcGamingWikiGames.Contains(game.Id))
                        {
                            pendingMetadataExecutablePath = null;
                            break;
                        }

                        var pcGamingWikiLink = game.Links?
                            .FirstOrDefault(link =>
                                string.Equals(
                                    link.Name,
                                    "PCGamingWiki",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );

                        if (pcGamingWikiLink == null)
                        {
                            logger.Info(
                                $"PCGamingWiki link not available yet for: {game.Name}"
                            );

                            break;
                        }

                        if (string.IsNullOrWhiteSpace(
                            pcGamingWikiLink.Url))
                        {
                            logger.Warn(
                                $"PCGamingWiki link has no URL for: {game.Name}"
                            );

                            break;
                        }

                        logger.Info(
                            $"PCGamingWiki link found for {game.Name}: {pcGamingWikiLink.Url}"
                        );

                        openedPcGamingWikiGames.Add(game.Id);

                        try
                        {
                            Process.Start(
                                new ProcessStartInfo
                                {
                                    FileName =
                                        pcGamingWikiLink.Url,

                                    UseShellExecute = true
                                }
                            );

                            logger.Info(
                                $"Opened PCGamingWiki page for: {game.Name}"
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.Error(
                                ex,
                                $"Failed to open PCGamingWiki for: {game.Name}"
                            );
                        }

                        pendingMetadataExecutablePath = null;

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed during library update processing"
                );
            }
        }

        // ============================================================
        // CLEANUP
        // ============================================================

        ~AutoImport()
        {
            try
            {
                launcherCheckTimer?.Dispose();
                launcherProcess?.Dispose();
            }
            catch
            {
            }
        }

        // ============================================================
        // EXISTING GAMES
        // ============================================================

        private HashSet<string> BuildExistingGamesSet()
        {
            var existingSet = new HashSet<string>();

            try
            {
                foreach (var game in PlayniteApi.Database.Games)
                {
                    if (game.IsInstalled)
                    {
                        if (!string.IsNullOrEmpty(
                            game.InstallDirectory))
                        {
                            existingSet.Add(
                                NormalizePath(
                                    game.InstallDirectory
                                )
                            );
                        }

                        if (game.GameActions != null)
                        {
                            foreach (var action in game.GameActions)
                            {
                                if (action.Type ==
                                        GameActionType.File &&
                                    !string.IsNullOrEmpty(
                                        action.Path))
                                {
                                    existingSet.Add(
                                        NormalizePath(
                                            action.Path
                                        )
                                    );
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to build existing games set"
                );

                return new HashSet<string>();
            }

            return existingSet;
        }

        // ============================================================
        // PATH NORMALIZATION
        // ============================================================

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            return path
                .ToLowerInvariant()
                .Trim()
                .Replace("/", "\\");
        }

        // ============================================================
        // SCAN
        // ============================================================

        private List<GameMetadata> ScanAndSelectGames()
        {
            var allFoundGames =
                new List<ScannedGameWrapper>();

            if (settings.Settings.ScanFolders == null)
                return new List<GameMetadata>();

            var blockedSet =
                new HashSet<string>();

            if (settings.Settings.BlockedPaths != null)
            {
                foreach (var path in settings.Settings.BlockedPaths)
                {
                    blockedSet.Add(
                        NormalizePath(path)
                    );
                }
            }

            var existingSet =
                BuildExistingGamesSet();

            foreach (var folder in settings.Settings.ScanFolders)
            {
                if (Directory.Exists(folder))
                {
                    allFoundGames.AddRange(
                        ScanFolderLimited(
                            folder,
                            blockedSet,
                            existingSet
                        )
                    );
                }
            }

            if (allFoundGames.Count == 0)
                return new List<GameMetadata>();

            List<GameMetadata> finalSelection =
                new List<GameMetadata>();

            Application.Current.Dispatcher.Invoke(() =>
            {
                var window =
                    new GameSelectionWindow(
                        allFoundGames
                    );

                if (Application.Current.MainWindow != null)
                {
                    window.Owner =
                        Application.Current.MainWindow;
                }

                if (window.ShowDialog() == true)
                {
                    var selectedGames =
                        window.SelectedGames;

                    finalSelection =
                        selectedGames
                            .Select(game => game.GameData)
                            .ToList();

                    // ====================================================
                    // METADATA TRACKING
                    // ====================================================

                    if (selectedGames.Count > 0)
                    {
                        pendingMetadataExecutablePath =
                            selectedGames[0].ExecutablePath;

                        logger.Info(
                            $"Waiting for metadata for: {pendingMetadataExecutablePath}"
                        );
                    }

                    // ====================================================
                    // HDR
                    // ====================================================

                    if (window.EnableHdrSupport &&
                        selectedGames.Count > 0)
                    {
                        pendingHdrExecutablePath =
                            selectedGames[0].ExecutablePath;

                        logger.Info(
                            $"HDR requested for: {pendingHdrExecutablePath}"
                        );
                    }

                    // ====================================================
                    // DS4WINDOWS AUTO PROFILE
                    // ====================================================

                    if (selectedGames.Count > 0)
                    {
                        UpdateDS4WindowsProfiles(
                            selectedGames,
                            window.ControllerSelection
                        );
                    }

                    // ====================================================
                    // RESHADE DEPLOYER
                    // ====================================================

                    if (selectedGames.Count > 0)
                    {
                        LaunchReShadeDeployer(
                            selectedGames[0].ExecutablePath
                        );
                    }

                    // ====================================================
                    // IGNORED GAMES
                    // ====================================================

                    var newlyIgnored =
                        allFoundGames
                            .Where(game => game.IsIgnored)
                            .Select(game =>
                                game.ExecutablePath)
                            .ToList();

                    if (newlyIgnored.Count > 0)
                    {
                        foreach (var path in newlyIgnored)
                        {
                            if (!settings.BlockedPathsUI
                                .Contains(path))
                            {
                                settings.BlockedPathsUI
                                    .Add(path);
                            }
                        }

                        settings.EndEdit();
                    }
                }
            });

            return finalSelection;
        }

        // ============================================================
        // DS4WINDOWS AUTO PROFILES
        // ============================================================

        private void UpdateDS4WindowsProfiles(
            List<ScannedGameWrapper> selectedGames,
            string controllerSelection)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(controllerSelection))
                {
                    logger.Warn(
                        "No DS4Windows controller selection was provided."
                    );

                    return;
                }

                if (!File.Exists(DS4WindowsAutoProfilesPath))
                {
                    logger.Warn(
                        $"DS4Windows Auto Profiles.xml not found: {DS4WindowsAutoProfilesPath}"
                    );

                    return;
                }

                // ========================================================
                // NORMALISATION DU CHOIX
                // ========================================================

                string controllerValue;
                bool turnOff;

                switch (controllerSelection.ToUpperInvariant())
                {
                    case "PS4":
                        controllerValue = "PS4";
                        turnOff = false;
                        break;

                    case "XBOX":
                        controllerValue = "xbox";
                        turnOff = false;
                        break;

                    case "OFF":
                        controllerValue = "(none)";
                        turnOff = true;
                        break;

                    default:
                        logger.Warn(
                            $"Unknown DS4Windows controller selection: {controllerSelection}"
                        );

                        return;
                }

                // ========================================================
                // DS4WINDOWS DOIT ÊTRE FERME AVANT MODIFICATION
                // ========================================================

                StopDS4Windows();

                // Petite sécurité pour laisser le processus disparaître.
                System.Threading.Thread.Sleep(500);

                // ========================================================
                // CHARGEMENT DU XML
                // ========================================================

                var xml =
                    new XmlDocument();

                xml.PreserveWhitespace = false;

                xml.Load(
                    DS4WindowsAutoProfilesPath
                );

                XmlElement programs =
                    xml.DocumentElement;

                if (programs == null ||
                    programs.Name != "Programs")
                {
                    logger.Warn(
                        "Invalid DS4Windows Auto Profiles.xml: missing <Programs> root."
                    );

                    RestartDS4Windows();

                    return;
                }

                // ========================================================
                // AJOUT / MISE A JOUR DES JEUX
                // ========================================================

                foreach (var scannedGame in selectedGames)
                {
                    if (scannedGame == null)
                        continue;

                    string executablePath =
                        scannedGame.ExecutablePath;

                    if (string.IsNullOrWhiteSpace(
                        executablePath))
                    {
                        continue;
                    }

                    string normalizedTarget =
                        NormalizePath(executablePath);

                    XmlElement existingProgram = null;

                    foreach (XmlNode node in programs.ChildNodes)
                    {
                        if (node is not XmlElement program)
                            continue;

                        if (program.Name != "Program")
                            continue;

                        string path =
                            program.GetAttribute("path");

                        if (NormalizePath(path) ==
                            normalizedTarget)
                        {
                            existingProgram = program;
                            break;
                        }
                    }

                    XmlElement programElement;

                    if (existingProgram != null)
                    {
                        programElement =
                            existingProgram;

                        logger.Info(
                            $"Updating existing DS4Windows profile for: {executablePath}"
                        );
                    }
                    else
                    {
                        programElement =
                            xml.CreateElement("Program");

                        programElement.SetAttribute(
                            "path",
                            executablePath
                        );

                        programElement.SetAttribute(
                            "title",
                            ""
                        );

                        programs.AppendChild(
                            programElement
                        );

                        logger.Info(
                            $"Adding new DS4Windows profile for: {executablePath}"
                        );
                    }

                    // ====================================================
                    // CONTROLLER 1
                    // ====================================================

                    SetXmlElementValue(
                        xml,
                        programElement,
                        "Controller1",
                        controllerValue
                    );

                    // ====================================================
                    // CONTROLLERS 2-8
                    // ====================================================

                    for (int i = 2; i <= 8; i++)
                    {
                        SetXmlElementValue(
                            xml,
                            programElement,
                            $"Controller{i}",
                            "(none)"
                        );
                    }

                    // ====================================================
                    // TURN OFF
                    // ====================================================

                    SetXmlElementValue(
                        xml,
                        programElement,
                        "TurnOff",
                        turnOff ? "True" : "False"
                    );
                }

                // ========================================================
                // SAUVEGARDE
                // ========================================================

                var settings =
                    new XmlWriterSettings
                    {
                        Indent = true,
                        IndentChars = "  ",
                        NewLineChars = Environment.NewLine,
                        NewLineHandling =
                            NewLineHandling.Entitize,
                        OmitXmlDeclaration = false
                    };

                using (var writer =
                    XmlWriter.Create(
                        DS4WindowsAutoProfilesPath,
                        settings))
                {
                    xml.Save(writer);
                }

                logger.Info(
                    $"DS4Windows Auto Profiles.xml updated successfully for user {WindowsUserName}."
                );

                // ========================================================
                // RELANCE DS4WINDOWS
                // ========================================================

                RestartDS4Windows();
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to update DS4Windows Auto Profiles.xml"
                );

                // Même en cas d'erreur, on essaye de remettre
                // DS4Windows en fonctionnement.
                try
                {
                    RestartDS4Windows();
                }
                catch
                {
                }
            }
        }

        // ============================================================
        // SET XML ELEMENT VALUE
        // ============================================================

        private void SetXmlElementValue(
            XmlDocument xml,
            XmlElement parent,
            string elementName,
            string value)
        {
            XmlElement element =
                null;

            foreach (XmlNode node in parent.ChildNodes)
            {
                if (node is XmlElement child &&
                    child.Name == elementName)
                {
                    element = child;
                    break;
                }
            }

            if (element == null)
            {
                element =
                    xml.CreateElement(elementName);

                parent.AppendChild(element);
            }

            element.InnerText = value;
        }

        // ============================================================
        // STOP DS4WINDOWS
        // ============================================================

        private void StopDS4Windows()
        {
            try
            {
                var processes =
                    Process.GetProcessesByName(
                        "DS4Windows"
                    );

                if (processes.Length == 0)
                {
                    logger.Info(
                        "DS4Windows is not currently running."
                    );

                    return;
                }

                foreach (var process in processes)
                {
                    try
                    {
                        logger.Info(
                            $"Stopping DS4Windows process PID {process.Id}."
                        );

                        process.Kill();

                        process.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn(
                            ex,
                            $"Failed to stop DS4Windows process PID {process.Id}."
                        );
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    "Failed while stopping DS4Windows."
                );
            }
        }

        // ============================================================
        // RESTART DS4WINDOWS
        // ============================================================

        private void RestartDS4Windows()
        {
            try
            {
                if (!File.Exists(DS4WindowsPath))
                {
                    logger.Warn(
                        $"DS4Windows executable not found: {DS4WindowsPath}"
                    );

                    return;
                }

                var startInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            DS4WindowsPath,

                        WorkingDirectory =
                            Path.GetDirectoryName(
                                DS4WindowsPath
                            ),

                        UseShellExecute = true
                    };

                Process.Start(
                    startInfo
                );

                logger.Info(
                    $"DS4Windows restarted successfully for user {WindowsUserName}."
                );
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to restart DS4Windows."
                );
            }
        }

        // ============================================================
        // RESHADE DEPLOYER
        // ============================================================

        private void LaunchReShadeDeployer(
            string executablePath)
        {
            try
            {
                if (!File.Exists(ReShadeDeployerPath))
                {
                    logger.Warn(
                        $"ReShade Deployer not found: {ReShadeDeployerPath}"
                    );

                    return;
                }

                if (string.IsNullOrWhiteSpace(
                    executablePath) ||
                    !File.Exists(executablePath))
                {
                    logger.Warn(
                        $"Target executable not found: {executablePath}"
                    );

                    return;
                }

                var startInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            ReShadeDeployerPath,

                        Arguments =
                            $"\"{executablePath}\"",

                        UseShellExecute = true
                    };

                Process.Start(startInfo);

                logger.Info(
                    $"Started ReShade Deployer for: {executablePath}"
                );
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    $"Failed to start ReShade Deployer for: {executablePath}"
                );
            }
        }

        // ============================================================
        // RECURSIVE FOLDER SCAN
        // ============================================================

        private IEnumerable<ScannedGameWrapper>
            ScanFolderLimited(
                string rootPath,
                HashSet<string> blockedSet,
                HashSet<string> existingSet)
        {
            var results =
                new List<ScannedGameWrapper>();

            results.AddRange(
                GetExecutablesInDir(
                    rootPath,
                    blockedSet,
                    existingSet
                )
            );

            try
            {
                foreach (var subDir in
                    Directory.GetDirectories(rootPath))
                {
                    results.AddRange(
                        ScanFolderLimited(
                            subDir,
                            blockedSet,
                            existingSet
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    $"Failed to scan subdirectories in: {rootPath}"
                );
            }

            return results;
        }

        // ============================================================
        // FIND EXE
        // ============================================================

        private IEnumerable<ScannedGameWrapper>
            GetExecutablesInDir(
                string dirPath,
                HashSet<string> blockedSet,
                HashSet<string> existingSet)
        {
            var list =
                new List<ScannedGameWrapper>();

            try
            {
                var files =
                    Directory.GetFiles(
                        dirPath,
                        "*.exe"
                    );

                foreach (var file in files)
                {
                    string normalizedFile =
                        NormalizePath(file);

                    string normalizedDir =
                        NormalizePath(dirPath);

                    bool isIgnored =
                        blockedSet.Contains(
                            normalizedFile
                        ) ||
                        blockedSet.Contains(
                            normalizedDir
                        );

                    if (isIgnored)
                        continue;

                    bool alreadyExists =
                        existingSet.Contains(
                            normalizedFile
                        ) ||
                        existingSet.Contains(
                            normalizedDir
                        );

                    if (alreadyExists)
                        continue;

                    if (IsGameExecutable(file))
                    {
                        var fileInfo =
                            new FileInfo(file);

                        string gameName =
                            GetGameNameFromFolder(
                                dirPath
                            );

                        var metadata =
                            new GameMetadata
                            {
                                Name = gameName,

                                GameId =
                                    fileInfo.FullName,

                                InstallDirectory =
                                    fileInfo.DirectoryName,

                                IsInstalled = true,

                                Platforms =
                                    new HashSet<MetadataProperty>
                                    {
                                        new MetadataSpecProperty(
                                            "pc_windows"
                                        )
                                    },

                                Source =
                                    new MetadataNameProperty(
                                        "AutoImport"
                                    ),

                                GameActions =
                                    new List<GameAction>
                                    {
                                        new GameAction
                                        {
                                            Type =
                                                GameActionType.File,

                                            Path =
                                                fileInfo.FullName,

                                            WorkingDir =
                                                fileInfo.DirectoryName,

                                            Name =
                                                "Play",

                                            IsPlayAction =
                                                true
                                        }
                                    }
                            };

                        list.Add(
                            new ScannedGameWrapper
                            {
                                GameData =
                                    metadata
                            }
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    $"Failed to scan directory for executables: {dirPath}"
                );
            }

            return list;
        }

        // ============================================================
        // GAME NAME
        // ============================================================

        private string GetGameNameFromFolder(
            string dirPath)
        {
            string folderName =
                Path.GetFileName(
                    dirPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    )
                );

            if (string.IsNullOrWhiteSpace(folderName))
            {
                return "Unknown Game";
            }

            string cleanFolderName =
                CleanGameName(folderName);

            if (!string.IsNullOrWhiteSpace(cleanFolderName))
            {
                return cleanFolderName;
            }

            return folderName.Trim();
        }

        // ============================================================
        // VALID GAME NAME
        // ============================================================

        private bool IsValidGameName(
            string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (name.Length < 2)
                return false;

            string lowerName =
                name.ToLowerInvariant();

            string[] genericNames =
            {
                "bin",
                "game",
                "games",
                "exe",
                "exes",
                "program",
                "programs",
                "application",
                "applications",
                "software",
                "tools",
                "util",
                "utils",
                "temp",
                "tmp",
                "download",
                "downloads"
            };

            return !genericNames.Contains(
                lowerName
            );
        }

        // ============================================================
        // CLEAN GAME NAME
        // ============================================================

        private string CleanGameName(
            string filename)
        {
            if (string.IsNullOrWhiteSpace(
                filename))
            {
                return filename;
            }

            string clean = filename.Trim();

            clean = clean
                .Replace('.', ' ')
                .Replace('_', ' ');

            clean = Regex.Replace(
                clean,
                @"\[.*?\]|\(.*?\)",
                " ",
                RegexOptions.IgnoreCase
            );

            string junkPattern =
                @"\bv?\d+(\.\d+)+\b" +
                @"|\brepack\b" +
                @"|\bgoty\b" +
                @"|\bx64\b" +
                @"|\bx86\b" +
                @"|\bbuild\b" +
                @"|\bsetup\b" +
                @"|\binstaller\b";

            clean = Regex.Replace(
                clean,
                junkPattern,
                " ",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bdirectors\s+cut\b",
                "Director's Cut",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bdirector\s+cut\b",
                "Director's Cut",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bgame\s+of\s+the\s+year\b",
                "Game of the Year",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bdefinitive\s+edition\b",
                "Definitive Edition",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bcomplete\s+edition\b",
                "Complete Edition",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bultimate\s+edition\b",
                "Ultimate Edition",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\bgoty\b",
                "Game of the Year",
                RegexOptions.IgnoreCase
            );

            clean = Regex.Replace(
                clean,
                @"\s+",
                " "
            ).Trim();

            if (string.IsNullOrWhiteSpace(clean))
                return string.Empty;

            if (clean == clean.ToUpperInvariant())
            {
                clean = ToTitleCasePreservingSpecialWords(
                    clean.ToLowerInvariant()
                );
            }

            return clean.Trim();
        }

        // ============================================================
        // TITLE CASE
        // ============================================================

        private string ToTitleCasePreservingSpecialWords(
            string text)
        {
            string[] words =
                text.Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries
                );

            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];

                if (string.IsNullOrWhiteSpace(word))
                    continue;

                if (word.Contains("'"))
                {
                    string[] parts =
                        word.Split(
                            new[] { '\'' },
                            StringSplitOptions.None
                        );

                    for (int j = 0; j < parts.Length; j++)
                    {
                        if (!string.IsNullOrEmpty(parts[j]))
                        {
                            parts[j] =
                                char.ToUpper(
                                    parts[j][0]
                                ) +
                                parts[j].Substring(1);
                        }
                    }

                    words[i] =
                        string.Join(
                            "'",
                            parts
                        );
                }
                else
                {
                    words[i] =
                        char.ToUpper(word[0]) +
                        word.Substring(1);
                }
            }

            string result =
                string.Join(
                    " ",
                    words
                );

            result = Regex.Replace(
                result,
                @"\bDirectors\s+Cut\b",
                "Director's Cut",
                RegexOptions.IgnoreCase
            );

            result = Regex.Replace(
                result,
                @"\bDirector\s+Cut\b",
                "Director's Cut",
                RegexOptions.IgnoreCase
            );

            result = Regex.Replace(
                result,
                @"\bGame\s+Of\s+The\s+Year\b",
                "Game of the Year",
                RegexOptions.IgnoreCase
            );

            return result;
        }

        // ============================================================
        // EXE FILTER
        // ============================================================

        private bool IsGameExecutable(
            string path)
        {
            string fileName =
                Path.GetFileName(path)
                    .ToLower();

            return !(
                fileName.Contains("uninstall") ||
                fileName.Contains("setup") ||
                fileName.Contains("config") ||
                fileName.Contains("crash")
            );
        }
    }
}
