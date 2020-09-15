using Gtk;
using LibHac.FsSystem;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.IO;
using Ryujinx.Common.IO.Abstractions;
using Ryujinx.Extensions;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.Loaders.Dlc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using GUI = Gtk.Builder.ObjectAttribute;
using JsonHelper = Ryujinx.Common.Utilities.JsonHelper;

namespace Ryujinx.Ui.Windows
{
    public class DlcWindow : Window
    {
        private readonly VirtualFileSystem _virtualFileSystem;
        private readonly string _titleId;
        private readonly string _dlcJsonPath;
        private readonly IList<DlcContainer> _dlcContainerList;
        private readonly ILocalStorageManagement _localStorageManagement;
        private readonly TreeStore _treeModel;

#pragma warning disable CS0649, IDE0044
        [GUI] Label _baseTitleInfoLabel;
        [GUI] TreeView _dlcTreeView;
        [GUI] TreeSelection _dlcTreeSelection;
#pragma warning restore CS0649, IDE0044

        private enum TreeStoreColumn
        {
            Enabled = 0,
            TitleId = 1,
            Path = 2
        }

        public DlcWindow(string titleId, string titleName, VirtualFileSystem virtualFileSystem) : this(new Builder("Ryujinx.Ui.Windows.DlcWindow.glade"), titleId, titleName, virtualFileSystem) { }

        private DlcWindow(Builder builder, string titleId, string titleName, VirtualFileSystem virtualFileSystem) : base(builder.GetObject("_dlcWindow").Handle)
        {
            builder.Autoconnect(this);

            _titleId = titleId;
            _virtualFileSystem = virtualFileSystem;
            _dlcJsonPath = System.IO.Path.Combine(AppDataManager.GamesDirPath, _titleId, "dlc.json");
            _baseTitleInfoLabel.Text = $"DLC Available for {titleName} [{titleId.ToUpper()}]";
            _localStorageManagement = new LocalStorageManagement();

            var dlcContainerLoader = new DlcContainerLoader(_dlcJsonPath, _localStorageManagement);

            try
            {
                _dlcContainerList = dlcContainerLoader.Load();
            }
            catch
            {
                _dlcContainerList = new List<DlcContainer>();
            }

            _treeModel = new TreeStore(typeof(bool), typeof(string), typeof(string));

            LoadDlcTree();
        }

        private void LoadDlcTree()
        {
            _dlcTreeView.Model = _treeModel;

            _dlcTreeView.AppendColumn(TreeStoreColumn.Enabled.ToString(), GetEnableCellRenderToggle(), "active", (int)TreeStoreColumn.Enabled);
            _dlcTreeView.AppendColumn(TreeStoreColumn.TitleId.ToString(), new CellRendererText(), "text", (int)TreeStoreColumn.TitleId);
            _dlcTreeView.AppendColumn(TreeStoreColumn.Path.ToString(), new CellRendererText(), "text", (int)TreeStoreColumn.Path);

            foreach (var dlcContainer in _dlcContainerList)
            {
                var parentIter = _treeModel.AppendValues(false, "", dlcContainer.Path);

                using FileStream containerFile = File.OpenRead(dlcContainer.Path);
                PartitionFileSystem pfs = new PartitionFileSystem(containerFile.AsStorage());
                _virtualFileSystem.ImportTickets(pfs);

                var allChildrenEnabled = true;

                var dlcNcaLoader = new DlcNcaLoader(_titleId, dlcContainer.Path, _localStorageManagement, _virtualFileSystem);

                foreach (var dlcNca in dlcNcaLoader.Load())
                {
                    var savedDlcNca = dlcContainer.DlcNcaList.FirstOrDefault(d => d.TitleId == dlcNca.TitleId);

                    if (!savedDlcNca.Enabled)
                        allChildrenEnabled = false;

                    _treeModel.AppendValues(parentIter, savedDlcNca.Enabled, dlcNca.TitleId.ToString("X16"), dlcNca.Path);
                }

                _treeModel.SetValue(parentIter, (int)TreeStoreColumn.Enabled, allChildrenEnabled);
            }
        }

        private CellRendererToggle GetEnableCellRenderToggle()
        {
            var enableToggle = new CellRendererToggle();

            enableToggle.Toggled += (sender, args) =>
            {
                _treeModel.GetIter(out TreeIter treeIter, new TreePath(args.Path));
                var newValue = !(bool)_treeModel.GetValue(treeIter, (int)TreeStoreColumn.Enabled);
                _treeModel.SetValue(treeIter, (int)TreeStoreColumn.Enabled, newValue);

                if (_treeModel.IterHasChild(treeIter))
                    _treeModel.ForEachChildren(treeIter, c => _treeModel.SetValue(c, (int)TreeStoreColumn.Enabled, newValue));
                else
                {
                    if (_treeModel.IterParent(out var parentIter, treeIter))
                    {
                        var totalChildren = 0;
                        var totalEnabled = 0;

                        _treeModel.ForEachChildren(parentIter, c =>
                        {
                            totalChildren++;
                            if ((bool)_treeModel.GetValue(c, (int)TreeStoreColumn.Enabled))
                                totalEnabled++;
                        });

                        _treeModel.SetValue(parentIter, (int)TreeStoreColumn.Enabled, totalEnabled == totalChildren);
                    }
                }
            };

            return enableToggle;
        }

        private void AddButton_Clicked(object sender, EventArgs args)
        {
            using var fileChooser = new FileChooserDialog("Select DLC files", this, FileChooserAction.Open, "Cancel", ResponseType.Cancel, "Add", ResponseType.Accept)
            {
                SelectMultiple = true,
                Filter = new FileFilter()
            };
            fileChooser.SetPosition(WindowPosition.Center);
            fileChooser.Filter.AddPattern("*.nsp");

            if (fileChooser.Run() != (int)ResponseType.Accept)
                return;

            foreach (string containerPath in fileChooser.Filenames.Where(f => _localStorageManagement.Exists(f)))
            {
                var dlcLoader = new DlcNcaLoader(_titleId, containerPath, _localStorageManagement, _virtualFileSystem);

                var dlcNcas = dlcLoader.Load();
                if (!dlcNcas.Any())
                {
                    GtkDialog.CreateErrorDialog($"The file {containerPath} does not contain a DLC for the selected title!");
                    break;
                }

                TreeIter? parentIter = null;

                foreach (var nca in dlcNcas)
                {
                    parentIter ??= _treeModel.AppendValues(true, "", containerPath);
                    _treeModel.AppendValues(parentIter.Value, true, nca.TitleId.ToString("X16"), nca.Path);
                }
            }
        }

        private void RemoveButton_Clicked(object sender, EventArgs args)
        {
            if (_dlcTreeSelection.GetSelected(out ITreeModel treeModel, out TreeIter treeIter))
            {
                if (_dlcTreeView.Model.IterParent(out TreeIter parentIter, treeIter) && _dlcTreeView.Model.IterNChildren(parentIter) <= 1)
                {
                    ((TreeStore)treeModel).Remove(ref parentIter);
                }
                else
                {
                    ((TreeStore)treeModel).Remove(ref treeIter);
                }
            }
        }
        
        private void RemoveAllButton_Clicked(object sender, EventArgs args)
        {
            List<TreeIter> toRemove = new List<TreeIter>();

            if (_dlcTreeView.Model.GetIterFirst(out TreeIter iter))
            {
                do
                {
                    toRemove.Add(iter);
                } 
                while (_dlcTreeView.Model.IterNext(ref iter));
            }

            foreach (TreeIter i in toRemove)
            {
                TreeIter j = i;
                ((TreeStore)_dlcTreeView.Model).Remove(ref j);
            }
        }

        private void SaveButton_Clicked(object sender, EventArgs args)
        {
            _dlcContainerList.Clear();

            if (_dlcTreeView.Model.GetIterFirst(out TreeIter parentIter))
            {
                do
                {
                    if (_dlcTreeView.Model.IterChildren(out TreeIter childIter, parentIter))
                    {
                        DlcContainer dlcContainer = new DlcContainer
                        {
                            Path = (string)_dlcTreeView.Model.GetValue(parentIter, 2),
                            DlcNcaList = new List<DlcNca>()
                        };

                        do
                        {
                            dlcContainer.DlcNcaList.Add(new DlcNca(
                                enabled: (bool)_dlcTreeView.Model.GetValue(childIter, 0),
                                titleId: Convert.ToUInt64(_dlcTreeView.Model.GetValue(childIter, 1).ToString(), 16),
                                path: (string)_dlcTreeView.Model.GetValue(childIter, 2)
                            ));
                        }
                        while (_dlcTreeView.Model.IterNext(ref childIter));

                        _dlcContainerList.Add(dlcContainer);
                    }
                }
                while (_dlcTreeView.Model.IterNext(ref parentIter));
            }

            using (FileStream dlcJsonStream = File.Create(_dlcJsonPath, 4096, FileOptions.WriteThrough))
            {
                dlcJsonStream.Write(Encoding.UTF8.GetBytes(JsonHelper.Serialize(_dlcContainerList, true)));
            }

            Dispose();
        }

        private void CancelButton_Clicked(object sender, EventArgs args)
        {
            Dispose();
        }
    }
}