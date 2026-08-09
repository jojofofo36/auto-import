using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

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
                    .Where(game =>
                        game != null &&
                        game.IsSelected)
                    .ToList();
            }
        }

        // ============================================================
        // HDR
        // ============================================================

        public bool EnableHdrSupport
        {
            get
            {
                return ChkEnableHdr.IsChecked == true;
            }
        }

        // ============================================================
        // CONTROLLER
        // ============================================================

        public string SelectedController
        {
            get
            {
                if (CmbController.SelectedItem
                    is ComboBoxItem item)
                {
                    return item.Content?.ToString() ?? "OFF";
                }

                return "OFF";
            }
        }

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public GameSelectionWindow(
            List<ScannedGameWrapper> foundGames)
        {
            InitializeComponent();

            _games =
                foundGames ??
                new List<ScannedGameWrapper>();

            GridGames.ItemsSource =
                _games;

            CmbController.SelectedIndex =
                0;
        }

        // ============================================================
        // IMPORT
        // ============================================================

        private void BtnImport_Click(
            object sender,
            RoutedEventArgs e)
        {
            /*
             * Force la DataGrid à terminer toute éventuelle édition
             * avant de lire IsSelected.
             */

            GridGames.CommitEdit(
                DataGridEditingUnit.Cell,
                true
            );

            GridGames.CommitEdit(
                DataGridEditingUnit.Row,
                true
            );

            var selected =
                SelectedGames;

            if (selected.Count == 0)
            {
                MessageBox.Show(
                    "Please select at least one game to import.",
                    "AutoImport",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );

                return;
            }

            DialogResult = true;
        }

        // ============================================================
        // CANCEL
        // ============================================================

        private void BtnCancel_Click(
            object sender,
            RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
