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

        private System.Threading.Timer pendingImportTimer;

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
        // POST IMPORT
        // ============================================================

        private class PendingImportedGame
        {
            public string ExecutablePath { get; set; }

            public bool EnableHdr { get; set; }

            public string Controller { get; set; }

            public bool ReShadeStarted { get; set; }

            public bool Ds4Updated { get; set; }

            public bool PcGamingWikiOpened { get; set; }

            public bool MetadataReady { get; set; }
        }

        private readonly List<PendingImportedGame>
            pendingImportedGames =
                new List<PendingImportedGame>();

        private readonly object pendingImportLock =
            new object();

        private bool postImportTimerRunning;

        // ============================================================
        // PCGAMINGWIKI
        // ============================================================

        private readonly HashSet<Guid>
            openedPcGamingWikiGames =
            new HashSet<Guid>();

        // ============================================================
        // ID
        // ============================================================

        public override Guid Id { get; } =
            Guid.Parse(
                "92c96e54-069b-4fc4-bbaa-35ac3064f85a"
            );

        public override string Name =>
            "AutoImport";

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public AutoImport(IPlayniteAPI api)
            : base(api)
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

        // ============================================================
        // SETTINGS
        // ============================================================

        public override ISettings GetSettings(
            bool firstRunSettings)
        {
            return settings;
        }

        public override System.Windows.Controls.UserControl
            GetSettingsView(
                bool firstRunSettings)
        {
            return new AutoImportSettingsView();
        }

        // ============================================================
        // GET GAMES
        // ============================================================

        public override IEnumerable<GameMetadata> GetGames(
            LibraryGetGamesArgs args)
        {
            logger.Info(
                "AutoImport GetGames() called."
            );

            /*
             * IMPORTANT:
             *
             * GetGames() doit uniquement :
             *
             * 1. scanner
             * 2. afficher la fenêtre
             * 3. préparer la file post-import
             * 4. retourner les GameMetadata
             *
             * AUCUNE opération DS4/ReShade/PCGamingWiki n'est
             * lancée ici.
             *
             * Playnite doit d'abord recevoir et importer les jeux.
             */

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

            /*
             * IMPORTANT:
             *
             * Cette méthode est appelée APRÈS que Playnite ait
             * traité la liste retournée par GetGames().
             *
             * C'est donc ici que l'on commence le workflow
             * post-import.
             */

            try
            {
                logger.Info(
                    "Playnite library updated. Checking pending imports."
                );

                StartPostImportTimerIfNeeded();
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to process library update"
                );
            }
        }

        // ============================================================
        // POST IMPORT TIMER
        // ============================================================

        private void StartPostImportTimerIfNeeded()
        {
            lock (pendingImportLock)
            {
                if (pendingImportedGames.Count == 0)
                {
                    logger.Info(
                        "No pending imported games."
                    );

                    return;
                }

                if (postImportTimerRunning)
                    return;

                postImportTimerRunning = true;
            }

            logger.Info(
                "Starting post-import verification timer."
            );

            pendingImportTimer =
                new System.Threading.Timer(
                    ProcessPendingImports,
                    null,
                    1500,
                    1500
                );
        }

        // ============================================================
        // PROCESS PENDING IMPORTS
        // ============================================================

        private void ProcessPendingImports(object state)
        {
            try
            {
                if (Application.Current == null)
                    return;

                Application.Current.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        ProcessPendingImportsOnUiThread();
                    })
                );
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to dispatch post-import processing"
                );
            }
        }

        // ============================================================
        // PROCESS PENDING IMPORTS - UI THREAD
        // ============================================================

        private void ProcessPendingImportsOnUiThread()
        {
            try
            {
                List<PendingImportedGame> snapshot;

                lock (pendingImportLock)
                {
                    snapshot =
                        pendingImportedGames.ToList();
                }

                if (snapshot.Count == 0)
                {
                    StopPostImportTimer();
                    return;
                }

                foreach (var pending in snapshot)
                {
                    if (pending == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(
                        pending.ExecutablePath))
                    {
                        RemovePendingGame(pending);
                        continue;
                    }

                    string targetPath =
                        NormalizePath(
                            pending.ExecutablePath
                        );

                    var playniteGame =
                        FindGameByExecutablePath(
                            targetPath
                        );

                    // =================================================
                    // PLAYNITE N'A PAS ENCORE FINI
                    // =================================================

                    if (playniteGame == null)
                    {
                        logger.Info(
                            $"Waiting for Playnite to finish importing: {pending.ExecutablePath}"
                        );

                        continue;
                    }

                    logger.Info(
                        $"Game found in Playnite database: {playniteGame.Name}"
                    );

                    // =================================================
                    // DS4WINDOWS
                    // =================================================

                    if (!pending.Ds4Updated)
                    {
                        if (!string.IsNullOrWhiteSpace(
                            pending.Controller))
                        {
                            logger.Info(
                                $"Applying DS4Windows controller profile for: {playniteGame.Name}"
                            );

                            UpdateDS4WindowsProfile(
                                pending.ExecutablePath,
                                pending.Controller
                            );
                        }

                        pending.Ds4Updated = true;

                        logger.Info(
                            $"DS4Windows processing completed for: {playniteGame.Name}"
                        );
                    }

                    // =================================================
                    // RESHADE
                    // =================================================

                    if (!pending.ReShadeStarted)
                    {
                        LaunchReShadeDeployer(
                            pending.ExecutablePath
                        );

                        pending.ReShadeStarted = true;

                        logger.Info(
                            $"ReShade processing started for: {playniteGame.Name}"
                        );
                    }

                    // =================================================
                    // HDR
                    // =================================================

                    if (pending.EnableHdr)
                    {
                        if (!playniteGame.EnableSystemHdr)
                        {
                            playniteGame.EnableSystemHdr = true;

                            PlayniteApi.Database.Games.Update(
                                playniteGame
                            );

                            logger.Info(
                                $"Enabled system HDR for imported game: {playniteGame.Name}"
                            );
                        }

                        pending.EnableHdr = false;
                    }

                    // =================================================
                    // METADATA / PCGAMINGWIKI
                    // =================================================

                    if (!pending.PcGamingWikiOpened)
                    {
                        var pcGamingWikiLink =
                            playniteGame.Links?
                                .FirstOrDefault(
                                    link =>
                                        string.Equals(
                                            link.Name,
                                            "PCGamingWiki",
                                            StringComparison.OrdinalIgnoreCase
                                        )
                                );

                        if (pcGamingWikiLink == null)
                        {
                            logger.Info(
                                $"Waiting for metadata / PCGamingWiki link for: {playniteGame.Name}"
                            );

                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(
                            pcGamingWikiLink.Url))
                        {
                            logger.Warn(
                                $"PCGamingWiki link has no URL for: {playniteGame.Name}"
                            );

                            pending.PcGamingWikiOpened = true;

                            continue;
                        }

                        if (!openedPcGamingWikiGames.Contains(
                            playniteGame.Id))
                        {
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

                                openedPcGamingWikiGames.Add(
                                    playniteGame.Id
                                );

                                logger.Info(
                                    $"Opened PCGamingWiki page for: {playniteGame.Name}"
                                );
                            }
                            catch (Exception ex)
                            {
                                logger.Error(
                                    ex,
                                    $"Failed to open PCGamingWiki for: {playniteGame.Name}"
                                );

                                continue;
                            }
                        }

                        pending.PcGamingWikiOpened = true;
                    }

                    // =================================================
                    // FIN
                    // =================================================

                    if (pending.Ds4Updated &&
                        pending.ReShadeStarted &&
                        !pending.EnableHdr &&
                        pending.PcGamingWikiOpened)
                    {
                        RemovePendingGame(
                            pending
                        );

                        logger.Info(
                            $"Post-import processing completed for: {playniteGame.Name}"
                        );
                    }
                }

                lock (pendingImportLock)
                {
                    if (pendingImportedGames.Count == 0)
                    {
                        StopPostImportTimer();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed during post-import processing"
                );
            }
        }

        // ============================================================
        // FIND GAME
        // ============================================================

        private Game FindGameByExecutablePath(
            string normalizedTarget)
        {
            try
            {
                foreach (var game in
                    PlayniteApi.Database.Games)
                {
                    if (game.GameActions == null)
                        continue;

                    bool found =
                        game.GameActions.Any(
                            action =>
                                action.Type ==
                                    GameActionType.File &&
                                !string.IsNullOrEmpty(
                                    action.Path
                                ) &&
                                NormalizePath(
                                    action.Path
                                ) ==
                                normalizedTarget
                        );

                    if (found)
                        return game;
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    $"Failed to find game for executable: {normalizedTarget}"
                );
            }

            return null;
        }

        // ============================================================
        // REMOVE PENDING GAME
        // ============================================================

        private void RemovePendingGame(
            PendingImportedGame pending)
        {
            lock (pendingImportLock)
            {
                pendingImportedGames.Remove(
                    pending
                );
            }
        }

        // ============================================================
        // STOP TIMER
        // ============================================================

        private void StopPostImportTimer()
        {
            lock (pendingImportLock)
            {
                if (pendingImportedGames.Count > 0)
                    return;

                postImportTimerRunning = false;
            }

            try
            {
                pendingImportTimer?.Dispose();
                pendingImportTimer = null;
            }
            catch
            {
            }

            logger.Info(
                "Post-import verification timer stopped."
            );
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
                pendingImportTimer?.Dispose();
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

        private string NormalizePath(
            string path)
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

        private List<GameMetadata>
            ScanAndSelectGames()
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

            logger.Info(
                $"AutoImport found {allFoundGames.Count} candidate executable(s)."
            );

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

                    bool? result =
                        window.ShowDialog();

                    if (result != true)
                    {
                        logger.Info(
                            "Game selection window cancelled."
                        );

                        return;
                    }

                    var selectedGames =
                        window.SelectedGames;

                    if (selectedGames == null ||
                        selectedGames.Count == 0)
                    {
                        logger.Warn(
                            "Import clicked but no games were selected."
                        );

                        return;
                    }

                    logger.Info(
                        $"User selected {selectedGames.Count} game(s) for import."
                    );

                    string selectedController =
                        window.SelectedController;

                    bool enableHdr =
                        window.EnableHdrSupport;

                    // =================================================
                    // PREPARE POST-IMPORT DATA ONLY
                    //
                    // NOTHING IS STARTED HERE.
                    // =================================================

                    lock (pendingImportLock)
                    {
                        foreach (var selectedGame
                            in selectedGames)
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

                            bool alreadyPending =
                                pendingImportedGames.Any(
                                    x =>
                                        NormalizePath(
                                            x.ExecutablePath
                                        ) ==
                                        NormalizePath(
                                            executablePath
                                        )
                                );

                            if (alreadyPending)
                                continue;

                            pendingImportedGames.Add(
                                new PendingImportedGame
                                {
                                    ExecutablePath =
                                        executablePath,

                                    EnableHdr =
                                        enableHdr,

                                    Controller =
                                        selectedController,

                                    ReShadeStarted =
                                        false,

                                    Ds4Updated =
                                        false,

                                    PcGamingWikiOpened =
                                        false,

                                    MetadataReady =
                                        false
                                }
                            );

                            logger.Info(
                                $"Queued post-import processing for: {executablePath}"
                            );
                        }
                    }

                    // =================================================
                    // IGNORED GAMES
                    // =================================================

                    var newlyIgnored =
                        allFoundGames
                            .Where(
                                game =>
                                    game.IsIgnored
                            )
                            .Select(
                                game =>
                                    game.ExecutablePath
                            )
                            .ToList();

                    if (newlyIgnored.Count > 0)
                    {
                        foreach (var path
                            in newlyIgnored)
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

                    // =================================================
                    // CRITICAL
                    //
                    // We return the GameMetadata to Playnite NOW.
                    //
                    // Post-import processing is started later from
                    // OnLibraryUpdated().
                    // =================================================

                    finalSelection =
                        selectedGames
                            .Select(
                                game =>
                                    game.GameData
                            )
                            .ToList();

                    logger.Info(
                        $"Returning {finalSelection.Count} GameMetadata item(s) to Playnite."
                    );
                }
            );

            return finalSelection;
        }

        // ============================================================
        // DS4WINDOWS - UPDATE
        // ============================================================

        private void UpdateDS4WindowsProfile(
            string executablePath,
            string selectedController)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(
                    executablePath))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(
                    selectedController))
                {
                    logger.Info(
                        "No controller selected. DS4Windows profile was not modified."
                    );

                    return;
                }

                if (!File.Exists(
                    AutoProfilesPath))
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

                        controllerValue =
                            "PS4";

                        turnOff =
                            false;

                        break;

                    case "XBOX":

                        controllerValue =
                            "xbox";

                        turnOff =
                            false;

                        break;

                    case "OFF":

                        controllerValue =
                            "(none)";

                        turnOff =
                            true;

                        break;

                    default:

                        logger.Warn(
                            $"Unknown controller selection: {selectedController}"
                        );

                        return;
                }

                logger.Info(
                    $"Updating DS4Windows: Controller1={controllerValue}, TurnOff={turnOff}"
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

                string normalizedTarget =
                    NormalizePath(
                        executablePath
                    );

                XElement existingProgram =
                    programs
                        .Elements("Program")
                        .FirstOrDefault(
                            program =>
                            {
                                string path =
                                    (string)
                                        program.Attribute(
                                            "path"
                                        );

                                return NormalizePath(
                                    path
                                ) ==
                                normalizedTarget;
                            }
                        );

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

                using (
                    var killProcess =
                        Process.Start(
                            killInfo
                        )
                )
                {
                    if (killProcess != null)
                    {
                        killProcess.WaitForExit(
                            5000
                        );

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

                bool stillRunning =
                    true;

                for (int i = 0; i < 20; i++)
                {
                    var processes =
                        Process.GetProcessesByName(
                            DS4WindowsProcessName
                        );

                    stillRunning =
                        processes.Length > 0;

                    foreach (var process
                        in processes)
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

                foreach (
                    var process
                    in remainingProcesses)
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
                    !File.Exists(
                        executablePath))
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

                Process.Start(
                    startInfo
                );

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
                foreach (var subDir
                    in Directory.GetDirectories(
                        rootPath))
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
                        NormalizePath(
                            file
                        );

                    string normalizedDir =
                        NormalizePath(
                            dirPath
                        );

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

                    if (!IsGameExecutable(
                        file))
                    {
                        continue;
                    }

                    var fileInfo =
                        new FileInfo(
                            file
                        );

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
                CleanGameName(
                    folderName
                );

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
            if (string.IsNullOrWhiteSpace(
                name))
            {
                return false;
            }

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

            if (string.IsNullOrWhiteSpace(
                clean))
            {
                return string.Empty;
            }

            if (clean ==
                clean.ToUpperInvariant())
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

        private string
            ToTitleCasePreservingSpecialWords(
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
                        char.ToUpper(
                            word[0]
                        ) +
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
                Path.GetFileName(
                    path
                ).ToLower();

            return !(
                fileName.Contains("uninstall") ||
                fileName.Contains("setup") ||
                fileName.Contains("config") ||
                fileName.Contains("crash")
            );
        }
    }
}
