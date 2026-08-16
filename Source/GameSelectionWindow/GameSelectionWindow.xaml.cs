using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AutoImportPlugin
{
    public partial class GameSelectionWindow : Window
    {
        private List<ScannedGameWrapper> _games;

        // ============================================================
        // SELECTED GAMES
        // ============================================================

        public List<ScannedGameWrapper> SelectedGames
        {
            get
            {
                return _games
                    .Where(game => game.IsSelected)
                    .ToList();
            }
        }

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public GameSelectionWindow(
            List<ScannedGameWrapper> foundGames)
        {
            InitializeComponent();

            _games = foundGames;

            GridGames.ItemsSource = _games;
        }

        // ============================================================
        // IMPORT
        // ============================================================

        private void BtnImport_Click(
            object sender,
            RoutedEventArgs e)
        {
            bool hasSelectedGames =
                _games.Any(game => game.IsSelected);

            bool hasIgnoredGames =
                _games.Any(game => game.IsIgnored);

            // ========================================================
            // RIEN N'A ÉTÉ SÉLECTIONNÉ
            // ========================================================

            if (!hasSelectedGames && !hasIgnoredGames)
            {
                MessageBox.Show(
                    "Please select at least one game to import or ignore.",
                    "AutoImport",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                return;
            }

            // ========================================================
            // AU MOINS UN IMPORT OU UN IGNORE
            // ========================================================

            DialogResult = true;
            Close();
        }

        // ============================================================
        // CANCEL
        // ============================================================

        private void BtnCancel_Click(
            object sender,
            RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
