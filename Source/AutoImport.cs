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
        private static readonly ILogger logger = LogManager.GetLogger();

        private AutoImportSettingsViewModel settings { get; set; }

        private Process launcherProcess;
        private System.Threading.Timer launcherCheckTimer;

        private const string ReShadeDeployerPath =
            @"G:\Reshade\Reshade Deployer.exe";

        private const string DS4WindowsPath =
            @"C:\Program Files (x86)\net8.0-windows7.0.Full\DS4Windows.exe";

        private const string DS4WindowsProcessName =
            "DS4Windows";

        private const string AutoProfilesPath =
            @"C:\Program Files (x86)\net8.0-windows7.0.Full\Auto Profiles.xml";

        /*
         * IMPORTANT
         *
         * GetGames() ne lance AUCUNE action post-import.
         *
         * Playnite doit d'abord recevoir et importer les GameMetadata.
         *
         * On conserve ensuite ici les informations nécessaires pour
         * retrouver les jeux après que Playnite ait terminé.
         */

        private class PendingImportedGame
        {
            public string ExecutablePath { get; set; }
            public bool EnableHdr { get; set; }
            public string Controller { get; set; }

            public bool Ds4Done { get; set; }
            public bool ReShadeDone { get; set; }
            public bool PcGamingWikiDone { get; set; }

            public DateTime QueuedAt { get; set; }
        }

        private readonly List<PendingImportedGame> pendingImportedGames =
            new List<PendingImportedGame>();

        private readonly object pendingLock = new object();

        private System.Threading.Timer pendingTimer;

        private bool pendingTimerRunning;

        private readonly HashSet<Guid> openedPcGamingWikiGames =
            new HashSet<Guid>();

        public override Guid Id { get; } =
            Guid.Parse("92c96e54-069b-4fc4-bbaa-35ac3064f85a");

        public override string Name => "AutoImport";

        public AutoImport(IPlayniteAPI api)
            : base(api)
        {
            settings = new AutoImportSettingsViewModel(this);

            Properties = new LibraryPluginProperties
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

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override System.Windows.Controls.UserControl GetSettingsView(
            bool firstRunSettings)
        {
            return new AutoImportSettingsView();
        }

        // ============================================================
        // PLAYNITE IMPORT
        // ============================================================

        public override IEnumerable<GameMetadata> GetGames(
            LibraryGetGamesArgs args)
        {
            /*
             * TRÈS IMPORTANT :
             *
             * Cette méthode ne fait que :
             *
             * 1. scanner
             * 2. afficher la fenêtre
             * 3. préparer les informations post-import
             * 4. retourner les GameMetadata
             *
             * Aucun DS4Windows
             * Aucun ReShade
             * Aucun PCGamingWiki
             * Aucun Update de la database
             *
             * avant que Playnite ait réellement importé les jeux.
             */

            return ScanAndSelectGames();
        }

        // ============================================================
        // LAUNCHER
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
                            try
                            {
                                ScanAndSelectGames();
                            }
                            catch (Exception ex)
                            {
                                logger.Error(
                                    ex,
                                    "Failed to scan games after launcher closed."
                                );
                            }
                        })
                    );

                    return;
                }

                var processes =
                    Process.GetProcessesByName("Project GLD");

                if (processes.Length > 0)
                {
                    launcherProcess = processes[0];
                }

                foreach (var process in processes.Skip(1))
                    process.Dispose();
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    "Failed to monitor launcher process."
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
             * Playnite appelle ceci après avoir modifié sa bibliothèque.
             *
             * C'est ici qu'on démarre la vérification post-import.
             */

            StartPendingTimer();
        }

        // ============================================================
        // START TIMER
        // ============================================================

        private void StartPendingTimer()
        {
            lock (pendingLock)
            {
                if (pendingImportedGames.Count == 0)
                    return;

                if (pendingTimerRunning)
                    return;

                pendingTimerRunning = true;
            }

            logger.Info(
                "Starting AutoImport post-import timer."
            );

            pendingTimer =
                new System.Threading.Timer(
                    PendingTimerCallback,
                    null,
                    2000,
                    2000
                );
        }

        // ============================================================
        // TIMER CALLBACK
        // ============================================================

        private void PendingTimerCallback(object state)
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(
                    new Action(
                        ProcessPendingGames
                    )
                );
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to dispatch pending game processing."
                );
            }
        }

        // ============================================================
        // PROCESS PENDING
        // ============================================================

        private void ProcessPendingGames()
        {
            try
            {
                List<PendingImportedGame> games;

                lock (pendingLock)
                {
                    games =
                        pendingImportedGames.ToList();
                }

                if (games.Count == 0)
                {
                    StopPendingTimer();
                    return;
                }

                foreach (var pending in games)
                {
                    ProcessOnePendingGame(pending);
                }

                lock (pendingLock)
                {
                    if (pendingImportedGames.Count == 0)
                    {
                        StopPendingTimer();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Error while processing pending imported games."
                );
            }
        }

        // ============================================================
        // PROCESS ONE GAME
        // ============================================================

        private void ProcessOnePendingGame(
            PendingImportedGame pending)
        {
            if (pending == null)
                return;

            if (string.IsNullOrWhiteSpace(
                pending.ExecutablePath))
            {
                RemovePendingGame(pending);
                return;
            }

            string target =
                NormalizePath(
                    pending.ExecutablePath
                );

            Game game =
                FindGameByExecutablePath(
                    target
                );

            /*
             * PLAYNITE N'A PAS ENCORE FINI.
             *
             * On ne fait absolument rien.
             *
             * Le timer reviendra plus tard.
             */

            if (game == null)
            {
                logger.Info(
                    $"Waiting for Playnite import: {pending.ExecutablePath}"
                );

                return;
            }

            logger.Info(
                $"Playnite import confirmed: {game.Name}"
            );

            // ========================================================
            // DS4WINDOWS
            // ========================================================

            if (!pending.Ds4Done)
            {
                if (!string.IsNullOrWhiteSpace(
                    pending.Controller))
                {
                    logger.Info(
                        $"Applying DS4Windows profile: {game.Name}"
                    );

                    UpdateDS4WindowsProfile(
                        pending.ExecutablePath,
                        pending.Controller
                    );
                }

                pending.Ds4Done = true;
            }

            // ========================================================
            // RESHADE
            // ========================================================

            if (!pending.ReShadeDone)
            {
                LaunchReShadeDeployer(
                    pending.ExecutablePath
                );

                pending.ReShadeDone = true;
            }

            // ========================================================
            // HDR
            // ========================================================

            if (pending.EnableHdr)
            {
                if (!game.EnableSystemHdr)
                {
                    game.EnableSystemHdr = true;

                    PlayniteApi.Database.Games.Update(
                        game
                    );

                    logger.Info(
                        $"HDR enabled for: {game.Name}"
                    );
                }

                pending.EnableHdr = false;
            }

            // ========================================================
            // PCGAMINGWIKI
            //
            // On attend volontairement que le lien apparaisse.
            // Cela laisse à Playnite / metadata providers le temps
            // de terminer leur travail.
            // ========================================================

            if (!pending.PcGamingWikiDone)
            {
                var link =
                    game.Links?
                        .FirstOrDefault(
                            x =>
                                string.Equals(
                                    x.Name,
                                    "PCGamingWiki",
                                    StringComparison.OrdinalIgnoreCase
                                )
                        );

                if (link == null)
                {
                    logger.Info(
                        $"Waiting for metadata / PCGamingWiki link: {game.Name}"
                    );

                    return;
                }

                if (string.IsNullOrWhiteSpace(link.Url))
                {
                    logger.Warn(
                        $"PCGamingWiki link has no URL: {game.Name}"
                    );

                    pending.PcGamingWikiDone = true;
                }
                else
                {
                    if (!openedPcGamingWikiGames.Contains(
                        game.Id))
                    {
                        try
                        {
                            Process.Start(
                                new ProcessStartInfo
                                {
                                    FileName = link.Url,
                                    UseShellExecute = true
                                }
                            );

                            openedPcGamingWikiGames.Add(
                                game.Id
                            );

                            logger.Info(
                                $"Opened PCGamingWiki: {game.Name}"
                            );
                        }
                        catch (Exception ex)
                        {
                            logger.Error(
                                ex,
                                $"Failed to open PCGamingWiki: {game.Name}"
                            );

                            /*
                             * On ne bloque pas indéfiniment la file
                             * si Windows refuse l'ouverture.
                             */
                        }
                    }

                    pending.PcGamingWikiDone = true;
                }
            }

            // ========================================================
            // FIN
            // ========================================================

            if (pending.Ds4Done &&
                pending.ReShadeDone &&
                !pending.EnableHdr &&
                pending.PcGamingWikiDone)
            {
                logger.Info(
                    $"AutoImport post-processing completed: {game.Name}"
                );

                RemovePendingGame(
                    pending
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
                foreach (var game in PlayniteApi.Database.Games)
                {
                    if (game.GameActions == null)
                        continue;

                    foreach (var action in game.GameActions)
                    {
                        if (action.Type != GameActionType.File)
                            continue;

                        if (string.IsNullOrWhiteSpace(
                            action.Path))
                            continue;

                        if (NormalizePath(action.Path) ==
                            normalizedTarget)
                        {
                            return game;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    $"Failed to find game: {normalizedTarget}"
                );
            }

            return null;
        }

        // ============================================================
        // REMOVE
        // ============================================================

        private void RemovePendingGame(
            PendingImportedGame pending)
        {
            lock (pendingLock)
            {
                pendingImportedGames.Remove(
                    pending
                );
            }
        }

        // ============================================================
        // STOP TIMER
        // ============================================================

        private void StopPendingTimer()
        {
            lock (pendingLock)
            {
                if (pendingImportedGames.Count != 0)
                    return;

                pendingTimerRunning = false;
            }

            try
            {
                pendingTimer?.Dispose();
                pendingTimer = null;
            }
            catch
            {
            }

            logger.Info(
                "AutoImport post-import timer stopped."
            );
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

                    bool result =
                        window.ShowDialog() == true;

                    if (!result)
                    {
                        logger.Info(
                            "Game selection cancelled."
                        );

                        return;
                    }

                    var selectedGames =
                        window.SelectedGames;

                    if (selectedGames == null ||
                        selectedGames.Count == 0)
                    {
                        logger.Info(
                            "No games selected for import."
                        );

                        return;
                    }

                    string controller =
                        window.SelectedController;

                    bool enableHdr =
                        window.EnableHdrSupport;

                    /*
                     * IMPORTANT :
                     *
                     * On mémorise uniquement les informations.
                     *
                     * On ne cherche PAS encore le jeu dans Playnite.
                     * Il n'existe pas encore.
                     */

                    lock (pendingLock)
                    {
                        foreach (var selected in selectedGames)
                        {
                            if (selected == null)
                                continue;

                            string exe =
                                selected.ExecutablePath;

                            if (string.IsNullOrWhiteSpace(exe))
                                continue;

                            string normalized =
                                NormalizePath(exe);

                            bool exists =
                                pendingImportedGames.Any(
                                    x =>
                                        NormalizePath(
                                            x.ExecutablePath
                                        ) == normalized
                                );

                            if (exists)
                                continue;

                            pendingImportedGames.Add(
                                new PendingImportedGame
                                {
                                    ExecutablePath = exe,
                                    Controller = controller,
                                    EnableHdr = enableHdr,
                                    Ds4Done = false,
                                    ReShadeDone = false,
                                    PcGamingWikiDone = false,
                                    QueuedAt = DateTime.Now
                                }
                            );

                            logger.Info(
                                $"Queued for post-import: {exe}"
                            );
                        }
                    }

                    /*
                     * NE PAS démarrer ici le timer.
                     *
                     * On attend OnLibraryUpdated().
                     *
                     * Cela garantit que Playnite a d'abord reçu
                     * les GameMetadata retournés par GetGames().
                     */

                    var ignored =
                        allFoundGames
                            .Where(x => x.IsIgnored)
                            .Select(x => x.ExecutablePath)
                            .ToList();

                    foreach (var path in ignored)
                    {
                        if (!settings.BlockedPathsUI.Contains(path))
                        {
                            settings.BlockedPathsUI.Add(path);
                        }
                    }

                    if (ignored.Count > 0)
                        settings.EndEdit();

                    /*
                     * C'EST CETTE LISTE QUI EST RETOURNÉE À PLAYNITE.
                     */

                    finalSelection =
                        selectedGames
                            .Select(x => x.GameData)
                            .Where(x => x != null)
                            .ToList();

                    logger.Info(
                        $"Returning {finalSelection.Count} games to Playnite."
                    );
                }
            );

            return finalSelection;
        }

        // ============================================================
        // EXISTING GAMES
        // ============================================================

        private HashSet<string> BuildExistingGamesSet()
        {
            var result =
                new HashSet<string>();

            try
            {
                foreach (var game in PlayniteApi.Database.Games)
                {
                    if (!game.IsInstalled)
                        continue;

                    if (!string.IsNullOrWhiteSpace(
                        game.InstallDirectory))
                    {
                        result.Add(
                            NormalizePath(
                                game.InstallDirectory
                            )
                        );
                    }

                    if (game.GameActions == null)
                        continue;

                    foreach (var action in game.GameActions)
                    {
                        if (action.Type != GameActionType.File)
                            continue;

                        if (string.IsNullOrWhiteSpace(
                            action.Path))
                            continue;

                        result.Add(
                            NormalizePath(
                                action.Path
                            )
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to build existing games set."
                );
            }

            return result;
        }

        // ============================================================
        // PATH
        // ============================================================

        private string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            return path
                .Trim()
                .Replace("/", "\\")
                .ToLowerInvariant();
        }

        // ============================================================
        // DS4WINDOWS
        // ============================================================

        private void UpdateDS4WindowsProfile(
            string executablePath,
            string selectedController)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(
                    executablePath))
                    return;

                if (string.IsNullOrWhiteSpace(
                    selectedController))
                    return;

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
                            $"Unknown controller: {selectedController}"
                        );
                        return;
                }

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
                        "Invalid Auto Profiles.xml root."
                    );

                    return;
                }

                string normalizedTarget =
                    NormalizePath(executablePath);

                XElement program =
                    programs
                        .Elements("Program")
                        .FirstOrDefault(
                            x =>
                                NormalizePath(
                                    (string)x.Attribute("path")
                                ) == normalizedTarget
                        );

                if (program == null)
                {
                    program =
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
                                turnOff ? "True" : "False"
                            )
                        );

                    programs.Add(program);
                }
                else
                {
                    XElement controller =
                        program.Element("Controller1");

                    if (controller == null)
                    {
                        program.Add(
                            new XElement(
                                "Controller1",
                                controllerValue
                            )
                        );
                    }
                    else
                    {
                        controller.Value =
                            controllerValue;
                    }

                    XElement turnOffElement =
                        program.Element("TurnOff");

                    if (turnOffElement == null)
                    {
                        program.Add(
                            new XElement(
                                "TurnOff",
                                turnOff ? "True" : "False"
                            )
                        );
                    }
                    else
                    {
                        turnOffElement.Value =
                            turnOff ? "True" : "False";
                    }
                }

                document.Save(
                    AutoProfilesPath,
                    SaveOptions.DisableFormatting
                );

                logger.Info(
                    $"DS4Windows profile saved: {executablePath}"
                );

                RestartDS4Windows();
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to update DS4Windows profile."
                );
            }
        }

        // ============================================================
        // DS4 RESTART
        // ============================================================

        private void RestartDS4Windows()
        {
            try
            {
                var killInfo =
                    new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments =
                            "/F /T /IM DS4Windows.exe",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                using (var process =
                    Process.Start(killInfo))
                {
                    process?.WaitForExit(5000);
                }

                for (int i = 0; i < 20; i++)
                {
                    var processes =
                        Process.GetProcessesByName(
                            DS4WindowsProcessName
                        );

                    bool running =
                        processes.Length > 0;

                    foreach (var p in processes)
                        p.Dispose();

                    if (!running)
                        break;

                    System.Threading.Thread.Sleep(250);
                }

                if (!File.Exists(DS4WindowsPath))
                {
                    logger.Error(
                        $"DS4Windows not found: {DS4WindowsPath}"
                    );

                    return;
                }

                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = DS4WindowsPath,
                        WorkingDirectory =
                            Path.GetDirectoryName(
                                DS4WindowsPath
                            ),
                        UseShellExecute = true
                    }
                );

                logger.Info(
                    "DS4Windows restarted."
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
        // RESHADE
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
                        $"Executable not found: {executablePath}"
                    );

                    return;
                }

                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            ReShadeDeployerPath,

                        Arguments =
                            $"\"{executablePath}\"",

                        UseShellExecute = true
                    }
                );

                logger.Info(
                    $"ReShade Deployer started: {executablePath}"
                );
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    "Failed to start ReShade Deployer."
                );
            }
        }

        // ============================================================
        // SCAN FOLDER
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
                foreach (var dir in
                    Directory.GetDirectories(rootPath))
                {
                    results.AddRange(
                        ScanFolderLimited(
                            dir,
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
                    $"Failed scanning: {rootPath}"
                );
            }

            return results;
        }

        // ============================================================
        // EXE
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
                foreach (var file in
                    Directory.GetFiles(
                        dirPath,
                        "*.exe"))
                {
                    string normalizedFile =
                        NormalizePath(file);

                    string normalizedDir =
                        NormalizePath(dirPath);

                    if (blockedSet.Contains(
                            normalizedFile) ||
                        blockedSet.Contains(
                            normalizedDir))
                        continue;

                    if (existingSet.Contains(
                            normalizedFile) ||
                        existingSet.Contains(
                            normalizedDir))
                        continue;

                    if (!IsGameExecutable(file))
                        continue;

                    var info =
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
                                info.FullName,

                            InstallDirectory =
                                info.DirectoryName,

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
                                            info.FullName,

                                        WorkingDir =
                                            info.DirectoryName,

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
                            GameData = metadata
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                logger.Warn(
                    ex,
                    $"Failed scanning directory: {dirPath}"
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
            string folder =
                Path.GetFileName(
                    dirPath.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    )
                );

            if (string.IsNullOrWhiteSpace(folder))
                return "Unknown Game";

            string clean =
                CleanGameName(folder);

            return string.IsNullOrWhiteSpace(clean)
                ? folder.Trim()
                : clean;
        }

        // ============================================================
        // CLEAN NAME
        // ============================================================

        private string CleanGameName(
            string filename)
        {
            if (string.IsNullOrWhiteSpace(filename))
                return filename;

            string clean =
                filename.Trim()
                    .Replace('.', ' ')
                    .Replace('_', ' ');

            clean =
                Regex.Replace(
                    clean,
                    @"\[.*?\]|\(.*?\)",
                    " ",
                    RegexOptions.IgnoreCase
                );

            clean =
                Regex.Replace(
                    clean,
                    @"\bv?\d+(\.\d+)+\b|\brepack\b|\bgoty\b|\bx64\b|\bx86\b|\bbuild\b|\bsetup\b|\binstaller\b",
                    " ",
                    RegexOptions.IgnoreCase
                );

            clean =
                Regex.Replace(
                    clean,
                    @"\bdirectors\s+cut\b",
                    "Director's Cut",
                    RegexOptions.IgnoreCase
                );

            clean =
                Regex.Replace(
                    clean,
                    @"\bdirector\s+cut\b",
                    "Director's Cut",
                    RegexOptions.IgnoreCase
                );

            clean =
                Regex.Replace(
                    clean,
                    @"\bgame\s+of\s+the\s+year\b",
                    "Game of the Year",
                    RegexOptions.IgnoreCase
                );

            clean =
                Regex.Replace(
                    clean,
                    @"\bdefinitive\s+edition\b",
                    "Definitive Edition",
                    RegexOptions.IgnoreCase
                );

            clean =
                Regex.Replace(
                    clean,
                    @"\bcomplete\s+edition\b",
                    "Complete Edition",
                    RegexOptions.IgnoreCase
                );

            clean =
                Regex.Replace(
                    clean,
                    @"\bultimate\s+edition\b",
                    "Ultimate Edition",
                    RegexOptions.IgnoreCase
                );

            clean =
                Regex.Replace(
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

            return clean;
        }

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
                if (string.IsNullOrWhiteSpace(words[i]))
                    continue;

                if (words[i].Contains("'"))
                {
                    string[] parts =
                        words[i].Split(
                            new[] { '\'' },
                            StringSplitOptions.None
                        );

                    for (int j = 0; j < parts.Length; j++)
                    {
                        if (!string.IsNullOrEmpty(parts[j]))
                        {
                            parts[j] =
                                char.ToUpper(parts[j][0]) +
                                parts[j].Substring(1);
                        }
                    }

                    words[i] =
                        string.Join("'", parts);
                }
                else
                {
                    words[i] =
                        char.ToUpper(words[i][0]) +
                        words[i].Substring(1);
                }
            }

            string result =
                string.Join(" ", words);

            result =
                Regex.Replace(
                    result,
                    @"\bDirectors\s+Cut\b",
                    "Director's Cut",
                    RegexOptions.IgnoreCase
                );

            result =
                Regex.Replace(
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

        private bool IsGameExecutable(string path)
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

        // ============================================================
        // CLEANUP
        // ============================================================

        ~AutoImport()
        {
            try
            {
                launcherCheckTimer?.Dispose();
                launcherProcess?.Dispose();
                pendingTimer?.Dispose();
            }
            catch
            {
            }
        }
    }
}
