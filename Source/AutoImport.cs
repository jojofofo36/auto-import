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
using System.Xml.Linq;

namespace AutoImportPlugin
{
    public class AutoImport : LibraryPlugin
    {
        private static readonly ILogger logger =
            LogManager.GetLogger();

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

        private const string DS4WindowsProcessName =
            "DS4Windows";

        private const string AutoProfilesPath =
            @"C:\Program Files (x86)\net8.0-windows7.0.Full\Auto Profiles.xml";

        // ============================================================
        // PENDING DATA
        // ============================================================

        private string pendingHdrExecutablePath;

        private string pendingMetadataExecutablePath;

        private readonly HashSet<Guid> openedPcGamingWikiGames =
            new HashSet<Guid>();

        public override Guid Id { get; } =
            Guid.Parse("92c96e54-069b-4fc4-bbaa-35ac3064f85a");

        public override string Name =>
            "AutoImport";

        public AutoImport(IPlayniteAPI api) : base(api)
        {
            settings =
                new AutoImportSettingsViewModel(this);

            Properties =
                new LibraryPluginProperties
                {
                    HasSettings = true
                };

            launcherCheckTimer =
                new System.Threading.Timer(
                    CheckLauncherProcess,
                    null,
                    0,
                    1000
                );
        }

        public override ISettings GetSettings(
            bool firstRunSettings)
        {
            return settings;
        }

        public override System.Windows.Controls.UserControl
            GetSettingsView(bool firstRunSettings)
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
                    Process.GetProcessesByName(
                        "Project GLD"
                    );

                if (processes.Length > 0)
                {
                    launcherProcess =
                        processes[0];
                }

                foreach (var process in
                    processes.Skip(1))
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

                if (!string.IsNullOrEmpty(
                    pendingHdrExecutablePath))
                {
                    string targetPath =
                        NormalizePath(
                            pendingHdrExecutablePath
                        );

                    foreach (var game in
                        PlayniteApi.Database.Games)
                    {
                        if (game.GameActions == null)
                            continue;

                        bool isTargetGame =
                            game.GameActions.Any(action =>
                                action.Type ==
                                    GameActionType.File &&
                                !string.IsNullOrEmpty(
                                    action.Path) &&
                                NormalizePath(
                                    action.Path
                                ) == targetPath
                            );

                        if (!isTargetGame)
                            continue;

                        game.EnableSystemHdr =
                            true;

                        PlayniteApi.Database.Games.Update(
                            game
                        );

                        logger.Info(
                            $"Enabled system HDR for imported game: {game.Name}"
                        );

                        pendingHdrExecutablePath =
                            null;

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

                    foreach (var game in
                        PlayniteApi.Database.Games)
                    {
                        if (game.GameActions == null)
                            continue;

                        bool isTargetGame =
                            game.GameActions.Any(action =>
                                action.Type ==
                                    GameActionType.File &&
                                !string.IsNullOrEmpty(
                                    action.Path) &&
                                NormalizePath(
                                    action.Path
                                ) == targetPath
                            );

                        if (!isTargetGame)
                            continue;

                        // ------------------------------------------------
                        // Le jeu existe maintenant dans Playnite.
                        // ------------------------------------------------

                        if (openedPcGamingWikiGames.Contains(
                            game.Id))
                        {
                            pendingMetadataExecutablePath =
                                null;

                            break;
                        }

                        // ------------------------------------------------
                        // Recherche du lien PCGamingWiki
                        // ------------------------------------------------

                        var pcGamingWikiLink =
                            game.Links?
                                .FirstOrDefault(link =>
                                    string.Equals(
                                        link.Name,
                                        "PCGamingWiki",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                );

                        // Les métadonnées ne sont probablement
                        // pas encore arrivées.
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

                        openedPcGamingWikiGames.Add(
                            game.Id
                        );

                        try
                        {
                            Process.Start(
                                new ProcessStartInfo
                                {
                                    FileName =
                                        pcGamingWikiLink.Url,

                                    UseShellExecute =
                                        true
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

                        pendingMetadataExecutablePath =
                            null;

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
            var existingSet =
                new HashSet<string>();

            try
            {
                foreach (var game in
                    PlayniteApi.Database.Games)
                {
                    if (!game.IsInstalled)
                        continue;

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
                        foreach (var action in
                            game.GameActions)
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
                foreach (var path in
                    settings.Settings.BlockedPaths)
                {
                    blockedSet.Add(
                        NormalizePath(path)
                    );
                }
            }

            var existingSet =
                BuildExistingGamesSet();

            foreach (var folder in
                settings.Settings.ScanFolders)
            {
                if (!Directory.Exists(folder))
                    continue;

                allFoundGames.AddRange(
                    ScanFolderLimited(
                        folder,
                        blockedSet,
                        existingSet
                    )
                );
            }

            if (allFoundGames.Count == 0)
                return new List<GameMetadata>();

            List<GameMetadata> finalSelection =
                new List<GameMetadata>();

            Application.Current.Dispatcher.Invoke(
                () =>
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

                    if (window.ShowDialog() != true)
                        return;

                    var selectedGames =
                        window.SelectedGames;

                    if (selectedGames == null ||
                        selectedGames.Count == 0)
                    {
                        HandleIgnoredGames(
                            allFoundGames
                        );

                        return;
                    }

                    // ====================================================
                    // IMPORTANT :
                    // On importe maintenant DIRECTEMENT dans la base.
                    //
                    // Cela évite de dépendre du retour de GetGames()
                    // pour l'import.
                    //
                    // ImportGame(GameMetadata, LibraryPlugin) est
                    // l'API Playnite prévue pour cet usage.
                    // ====================================================

                    var importedGames =
                        new List<Game>();

                    foreach (var selectedGame in
                        selectedGames)
                    {
                        if (selectedGame == null ||
                            selectedGame.GameData == null)
                        {
                            continue;
                        }

                        try
                        {
                            string executablePath =
                                selectedGame.ExecutablePath;

                            logger.Info(
                                $"Importing game directly into Playnite: {executablePath}"
                            );

                            Game importedGame =
                                PlayniteApi.Database.ImportGame(
                                    selectedGame.GameData,
                                    this
                                );

                            if (importedGame != null)
                            {
                                importedGames.Add(
                                    importedGame
                                );

                                logger.Info(
                                    $"Successfully imported: {importedGame.Name} ({importedGame.Id})"
                                );
                            }
                            else
                            {
                                logger.Warn(
                                    $"Playnite returned null while importing: {executablePath}"
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.Error(
                                ex,
                                $"Failed to directly import game: {selectedGame.ExecutablePath}"
                            );
                        }
                    }

                    // ====================================================
                    // TRACKING
                    // ====================================================

                    if (selectedGames.Count > 0)
                    {
                        pendingMetadataExecutablePath =
                            selectedGames[0]
                                .ExecutablePath;

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
                            selectedGames[0]
                                .ExecutablePath;

                        logger.Info(
                            $"HDR requested for: {pendingHdrExecutablePath}"
                        );
                    }
                    else
                    {
                        pendingHdrExecutablePath =
                            null;
                    }

                    // ====================================================
                    // DS4WINDOWS
                    // ====================================================

                    string selectedController =
                        window.SelectedController;

                    if (!string.IsNullOrWhiteSpace(
                        selectedController))
                    {
                        UpdateDS4WindowsProfiles(
                            selectedGames,
                            selectedController
                        );
                    }
                    else
                    {
                        logger.Info(
                            "No controller selected. DS4Windows profile was not modified."
                        );
                    }

                    // ====================================================
                    // RESHADE
                    // ====================================================

                    LaunchReShadeDeployer(
                        selectedGames[0]
                            .ExecutablePath
                    );

                    // ====================================================
                    // IGNORED GAMES
                    // ====================================================

                    HandleIgnoredGames(
                        allFoundGames
                    );

                    // ====================================================
                    // IMPORTANT
                    //
                    // On NE retourne PAS les GameMetadata importés
                    // ici, sinon Playnite pourrait tenter de les
                    // importer une deuxième fois.
                    // ====================================================

                    finalSelection =
                        new List<GameMetadata>();
                }
            );

            return finalSelection;
        }

        // ============================================================
        // IGNORED GAMES
        // ============================================================

        private void HandleIgnoredGames(
            List<ScannedGameWrapper> allFoundGames)
        {
            if (allFoundGames == null)
                return;

            var newlyIgnored =
                allFoundGames
                    .Where(game => game.IsIgnored)
                    .Select(game =>
                        game.ExecutablePath)
                    .ToList();

            if (newlyIgnored.Count == 0)
                return;

            foreach (var path in newlyIgnored)
            {
                if (!settings.BlockedPathsUI.Contains(path))
                {
                    settings.BlockedPathsUI.Add(path);
                }
            }

            settings.EndEdit();
        }

        // ============================================================
        // DS4WINDOWS - UPDATE AUTO PROFILES.XML
        // ============================================================

        private void UpdateDS4WindowsProfiles(
            List<ScannedGameWrapper> selectedGames,
            string selectedController)
        {
            try
            {
                if (selectedGames == null ||
                    selectedGames.Count == 0)
                {
                    return;
                }

                if (!File.Exists(AutoProfilesPath))
                {
                    logger.Error(
                        $"Auto Profiles.xml not found: {AutoProfilesPath}"
                    );

                    return;
                }

                string controllerValue;
                bool turnOff;

                switch (
                    selectedController
                        .Trim()
                        .ToUpperInvariant())
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
                            $"Unknown controller selection: {selectedController}"
                        );

                        return;
                }

                logger.Info(
                    $"Updating DS4Windows profiles: Controller1={controllerValue}, TurnOff={turnOff}"
                );

                XDocument document =
                    XDocument.Load(
                        AutoProfilesPath,
                        LoadOptions.PreserveWhitespace
                    );

                XElement programs =
                    document.Root;

                if (programs == null ||
                    programs.Name != "Programs")
                {
                    logger.Error(
                        "Auto Profiles.xml has an invalid root element."
                    );

                    return;
                }

                bool xmlChanged = false;

                foreach (var selectedGame in
                    selectedGames)
                {
                    if (selectedGame == null)
                        continue;

                    string executablePath =
                        selectedGame.ExecutablePath;

                    if (string.IsNullOrWhiteSpace(
                        executablePath))
                    {
                        continue;
                    }

                    string normalizedTarget =
                        NormalizePath(
                            executablePath
                        );

                    XElement existingProgram =
                        programs
                            .Elements("Program")
                            .FirstOrDefault(program =>
                            {
                                string path =
                                    (string)
                                    program.Attribute("path");

                                return NormalizePath(path) ==
                                    normalizedTarget;
                            });

                    if (existingProgram == null)
                    {
                        existingProgram =
                            new XElement(
                                "Program",
                                new XAttribute(
                                    "path",
                                    executablePath
                                ),
                                new XAttribute(
                                    "title",
                                    ""
                                ),
                                new XElement(
                                    "Controller1",
                                    controllerValue
                                ),
                                new XElement(
                                    "Controller2",
                                    "(none)"
                                ),
                                new XElement(
                                    "Controller3",
                                    "(none)"
                                ),
                                new XElement(
                                    "Controller4",
                                    "(none)"
                                ),
                                new XElement(
                                    "Controller5",
                                    "(none)"
                                ),
                                new XElement(
                                    "Controller6",
                                    "(none)"
                                ),
                                new XElement(
                                    "Controller7",
                                    "(none)"
                                ),
                                new XElement(
                                    "Controller8",
                                    "(none)"
                                ),
                                new XElement(
                                    "TurnOff",
                                    turnOff
                                        ? "True"
                                        : "False"
                                )
                            );

                        programs.Add(
                            existingProgram
                        );

                        logger.Info(
                            $"Added DS4Windows Auto Profile for: {executablePath}"
                        );
                    }
                    else
                    {
                        XElement controller1 =
                            existingProgram.Element(
                                "Controller1"
                            );

                        if (controller1 == null)
                        {
                            existingProgram.Add(
                                new XElement(
                                    "Controller1",
                                    controllerValue
                                )
                            );
                        }
                        else
                        {
                            controller1.Value =
                                controllerValue;
                        }

                        XElement turnOffElement =
                            existingProgram.Element(
                                "TurnOff"
                            );

                        if (turnOffElement == null)
                        {
                            existingProgram.Add(
                                new XElement(
                                    "TurnOff",
                                    turnOff
                                        ? "True"
                                        : "False"
                                )
                            );
                        }
                        else
                        {
                            turnOffElement.Value =
                                turnOff
                                    ? "True"
                                    : "False";
                        }

                        logger.Info(
                            $"Updated DS4Windows Auto Profile for: {executablePath}"
                        );
                    }

                    xmlChanged = true;
                }

                if (!xmlChanged)
                {
                    logger.Info(
                        "No DS4Windows Auto Profile changes were necessary."
                    );

                    return;
                }

                document.Save(
                    AutoProfilesPath,
                    SaveOptions.DisableFormatting
                );

                logger.Info(
                    $"Saved DS4Windows Auto Profiles: {AutoProfilesPath}"
                );

                RestartDS4Windows();
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to update DS4Windows Auto Profiles.xml"
                );
            }
        }

        // ============================================================
        // DS4WINDOWS - RESTART
        // ============================================================

        private void RestartDS4Windows()
        {
            try
            {
                logger.Info(
                    "Stopping DS4Windows..."
                );

                var killInfo =
                    new ProcessStartInfo
                    {
                        FileName =
                            "taskkill.exe",

                        Arguments =
                            "/F /T /IM DS4Windows.exe",

                        UseShellExecute =
                            false,

                        CreateNoWindow =
                            true,

                        RedirectStandardOutput =
                            true,

                        RedirectStandardError =
                            true
                    };

                using (var killProcess =
                    Process.Start(killInfo))
                {
                    if (killProcess != null)
                    {
                        killProcess.WaitForExit(5000);

                        string output =
                            killProcess
                                .StandardOutput
                                .ReadToEnd();

                        string error =
                            killProcess
                                .StandardError
                                .ReadToEnd();

                        if (!string.IsNullOrWhiteSpace(
                            output))
                        {
                            logger.Info(
                                $"taskkill output: {output}"
                            );
                        }

                        if (!string.IsNullOrWhiteSpace(
                            error))
                        {
                            logger.Warn(
                                $"taskkill error: {error}"
                            );
                        }
                    }
                }

                bool stillRunning = true;

                for (int i = 0; i < 20; i++)
                {
                    var processes =
                        Process.GetProcessesByName(
                            DS4WindowsProcessName
                        );

                    stillRunning =
                        processes.Length > 0;

                    foreach (var process in
                        processes)
                    {
                        process.Dispose();
                    }

                    if (!stillRunning)
                        break;

                    System.Threading.Thread.Sleep(
                        250
                    );
                }

                var remainingProcesses =
                    Process.GetProcessesByName(
                        DS4WindowsProcessName
                    );

                bool ds4StillRunning =
                    remainingProcesses.Length > 0;

                foreach (var process in
                    remainingProcesses)
                {
                    process.Dispose();
                }

                if (ds4StillRunning)
                {
                    logger.Error(
                        "DS4Windows is still running after taskkill. Aborting restart."
                    );

                    return;
                }

                if (!File.Exists(
                    DS4WindowsPath))
                {
                    logger.Error(
                        $"DS4Windows executable not found: {DS4WindowsPath}"
                    );

                    return;
                }

                logger.Info(
                    "Starting DS4Windows..."
                );

                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            DS4WindowsPath,

                        WorkingDirectory =
                            Path.GetDirectoryName(
                                DS4WindowsPath
                            ),

                        UseShellExecute =
                            true
                    }
                );

                logger.Info(
                    "DS4Windows restarted successfully."
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
                if (!File.Exists(
                    ReShadeDeployerPath))
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

                        UseShellExecute =
                            true
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

                    if (!IsGameExecutable(file))
                        continue;

                    var fileInfo =
                        new FileInfo(file);

                    string gameName =
                        GetGameNameFromFolder(
                            dirPath
                        );

                    var metadata =
                        new GameMetadata
                        {
                            Name =
                                gameName,

                            GameId =
                                fileInfo.FullName,

                            InstallDirectory =
                                fileInfo.DirectoryName,

                            IsInstalled =
                                true,

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

            if (string.IsNullOrWhiteSpace(
                folderName))
            {
                return "Unknown Game";
            }

            string cleanFolderName =
                CleanGameName(folderName);

            if (!string.IsNullOrWhiteSpace(
                cleanFolderName))
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

            string clean =
                filename.Trim();

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
                clean =
                    ToTitleCasePreservingSpecialWords(
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

            for (int i = 0;
                 i < words.Length;
                 i++)
            {
                string word =
                    words[i];

                if (string.IsNullOrWhiteSpace(
                    word))
                {
                    continue;
                }

                if (word.Contains("'"))
                {
                    string[] parts =
                        word.Split(
                            new[] { '\'' },
                            StringSplitOptions.None
                        );

                    for (int j = 0;
                         j < parts.Length;
                         j++)
                    {
                        if (!string.IsNullOrEmpty(
                            parts[j]))
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
                    .ToLowerInvariant();

            return !(
                fileName.Contains("uninstall") ||
                fileName.Contains("setup") ||
                fileName.Contains("config") ||
                fileName.Contains("crash")
            );
        }
    }
}
