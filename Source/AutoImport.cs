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

namespace AutoImportPlugin
{
    public class AutoImport : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private AutoImportSettingsViewModel settings { get; set; }

        private Process launcherProcess;
        private System.Threading.Timer launcherCheckTimer;

        private const string ReShadeDeployerPath = @"G:\Reshade\Reshade Deployer.exe";

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
                // Si on surveille déjà un launcher, vérifier s'il est encore actif
                if (launcherProcess != null)
                {
                    if (!launcherProcess.HasExited)
                        return;

                    launcherProcess.Dispose();
                    launcherProcess = null;

                    // Le launcher vient de se fermer :
                    // déclencher automatiquement le scan.
                    Application.Current.Dispatcher.BeginInvoke(
                        new Action(() =>
                        {
                            ScanAndSelectGames();
                        })
                    );

                    return;
                }

                // ========================================================
                // CHANGE "launcher" ICI SI TON EXE A UN AUTRE NOM
                // ========================================================

                var processes = Process.GetProcessesByName("launcher");

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

                        bool isTargetGame = game.GameActions.Any(action =>
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

                        // Important :
                        // on efface immédiatement la demande HDR afin
                        // d'éviter de refaire Update() à chaque
                        // OnLibraryUpdated().
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

                        bool isTargetGame = game.GameActions.Any(action =>
                            action.Type == GameActionType.File &&
                            !string.IsNullOrEmpty(action.Path) &&
                            NormalizePath(action.Path) == targetPath
                        );

                        if (!isTargetGame)
                            continue;

                        // Si le lien a déjà été ouvert pour ce jeu,
                        // on ne fait plus rien.
                        if (openedPcGamingWikiGames.Contains(game.Id))
                        {
                            pendingMetadataExecutablePath = null;
                            break;
                        }

                        // Cherche uniquement le lien dont le nom est
                        // "PCGamingWiki".
                        var pcGamingWikiLink = game.Links?
                            .FirstOrDefault(link =>
                                string.Equals(
                                    link.Name,
                                    "PCGamingWiki",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );

                        // Le lien n'est pas encore arrivé.
                        // On conserve pendingMetadataExecutablePath
                        // pour vérifier lors du prochain update.
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
                                    FileName = pcGamingWikiLink.Url,
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

                        // Le jeu a été traité.
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
                            GetGameNameFromFolderOrExe(
                                dirPath,
                                fileInfo
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

        private string GetGameNameFromFolderOrExe(
            string dirPath,
            FileInfo fileInfo)
        {
            string folderName =
                Path.GetFileName(dirPath);

            if (!string.IsNullOrWhiteSpace(
                folderName))
            {
                string cleanFolderName =
                    CleanGameName(folderName);

                if (IsValidGameName(
                    cleanFolderName))
                {
                    return cleanFolderName;
                }
            }

            string rawExeName =
                Path.GetFileNameWithoutExtension(
                    fileInfo.Name
                );

            return CleanGameName(
                rawExeName
            );
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
                filename
                    .Replace('.', ' ')
                    .Replace('_', ' ');

            clean = Regex.Replace(
                clean,
                @"\[.*?\]|\(.*?\)",
                ""
            );

            string junkPattern =
                @"\bv?(\d+(\.\d+)+)\b|repack|goty|edition|remastered|x64|x86|build|setup|installer";

            clean = Regex.Replace(
                clean,
                junkPattern,
                "",
                RegexOptions.IgnoreCase
            );

            return Regex.Replace(
                clean,
                @"\s+",
                " "
            ).Trim();
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
