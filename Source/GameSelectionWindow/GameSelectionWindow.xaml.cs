using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AutoImportPlugin
{
    public partial class GameSelectionWindow : Window
    {
        private readonly List<ScannedGameWrapper> _games;

        public List<ScannedGameWrapper> SelectedGames
        {
            get
            {
                return _games
                    .Where(x => x != null && x.IsSelected)
                    .ToList();
            }
        }

        public bool EnableHdrSupport
        {
            get
            {
                return ChkEnableHdr.IsChecked == true;
            }
        }

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

        public GameSelectionWindow(
            List<ScannedGameWrapper> foundGames)
        {
            InitializeComponent();

            _games =
                foundGames ??
                new List<ScannedGameWrapper>();

            GridGames.ItemsSource = _games;

            CmbController.SelectedIndex = 0;
        }

        private void BtnImport_Click(
            object sender,
            RoutedEventArgs e)
        {
            /*
             * On force le DataGrid à terminer l'édition éventuelle
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
                _games
                    .Where(x => x != null && x.IsSelected)
                    .ToList();

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

        private void BtnCancel_Click(
            object sender,
            RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
