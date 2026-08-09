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
                    .Where(game => game.IsSelected)
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

            _games = foundGames;

            GridGames.ItemsSource = _games;

            // Valeur par défaut :
            // PS4
            CmbController.SelectedIndex = 0;
        }

        // ============================================================
        // IMPORT
        // ============================================================

        private void BtnImport_Click(
            object sender,
            RoutedEventArgs e)
        {
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
