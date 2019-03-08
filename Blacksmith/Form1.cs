using Blacksmith.Enums;
using Blacksmith.FileTypes;
using Blacksmith.Forms;
using Blacksmith.Games;
using Blacksmith.Three;
using OpenTK;
using OpenTK.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace Blacksmith
{
    public partial class Form1 : Form
    {
        private double[] zoomLevels = new double[]
        {
            .25f, .5f, .75f, 1, 1.5f, 2, 2.5f, 3, 4
        };
        private GLViewer gl;
        private EntryTreeNode odysseyNode;
        private EntryTreeNode originsNode;
        private EntryTreeNode steepNode;
        private FindDialog findDialog = null;
        private string currentImgPath;
        private List<Mesh> meshes = new List<Mesh>();
        private bool useAlpha = true;
        private EntryTreeNode nodeToSearchIn = null;

        public Form1()
        {
            InitializeComponent();
            treeView.MouseDown += (sender, args) => treeView.SelectedNode = treeView.GetNodeAt(args.X, args.Y);
        }

        #region Events
        private void Form1_Load(object sender, EventArgs e)
        {
            // hide the progress bar
            toolStripProgressBar.Visible = false;

            // load the games' directories into the tree view
            LoadGamesIntoTreeView();

            // set up the zoom dropdown in the Image viewer
            foreach (double z in zoomLevels)
            {
                ToolStripMenuItem item = new ToolStripMenuItem();
                item.Text = $"{z * 100}%";
                item.Click += new EventHandler(delegate (object s, EventArgs a)
                {
                    ChangeZoom(z);
                });
                zoomDropDownButton.DropDownItems.Add(item);
            }

            // create a GLControl and a GLViewer
            GLControl glControl = new GLControl(new GraphicsMode(32, 24, 0, 4), 3, 0, GraphicsContextFlags.ForwardCompatible);
            glControl.Dock = DockStyle.Fill;
            threeToolStripContainer.ContentPanel.Controls.Add(glControl);
            gl = new GLViewer(glControl);
            gl.BackgroundColor = Properties.Settings.Default.threeBG;
            gl.Init();
            glControl.Focus();

            // a timer to constantly render the 3D Viewer
            Timer t = new Timer();
            t.Interval = (int)(1 / 70f * 1000); // oddly results in 60 FPS
            t.Tick += new EventHandler(delegate (object s, EventArgs a)
            {
                gl.Render();
                cameraStripLabel.Text = $"Camera: {gl.Camera.Position} {gl.Camera.Orientation}";
                sceneInfoStripLabel.Text = string.Concat("Vertices: ", string.Format("{0:n0}", gl.Models.Select(x => x.VertexCount).Sum()), " | Faces: ", string.Format("{0:n0}", gl.Models.Select(x => x.FaceCount).Sum()), " | Meshes: ", gl.Models.Count);
            });
            t.Start();

            // load settings
            LoadSettings();

            // load the Utah teapot
            OBJModel teapot = OBJModel.LoadFromFile(Application.ExecutablePath + "\\..\\Shaders\\teapot.obj");
            teapot.CalculateNormals();
            gl.Models.Add(teapot);

            /*gl.TextRenderer.Clear(Color.Red);
            gl.TextRenderer.DrawString("Text", new Font(FontFamily.GenericMonospace, 24), Brushes.White, new PointF(100, 100));*/
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (pictureBox.Image != null)
                UpdatePictureBox(pictureBox.Image, false);
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // delete all files in the temporary path
            if (Properties.Settings.Default.deleteTemp)
            {
                foreach (string f in Directory.GetFiles(Helpers.GetTempPath()))
                    File.Delete(f);
            }
        }

        private void treeView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node == null)
                return;
            EntryTreeNode node = (EntryTreeNode)e.Node;

            if (node.Type == EntryTreeNodeType.FORGE) // populate the tree with the forge's entries
            {
                List<EntryTreeNode> nodes = null;
                string[] names = null;

                treeView.Enabled = false; // prevent user-induced damages

                // work in the background
                Helpers.DoBackgroundWork(() =>
                {
                    nodes = PopulateForge(node);
                    if (nodes != null)
                        names = nodes.Select(x => x.Text).ToArray();
                }, () =>
                {
                    // prevent a crash
                    if (nodes == null || names == null)
                    {
                        treeView.Enabled = true;
                        return;
                    }

                    node.Nodes.AddRange(nodes.ToArray());
                    node.Nodes[0].Remove(); // remove the placeholder node
                    node.Tag = names;

                    treeView.Enabled = true;
                    MessageBox.Show($"Loaded the entries from {node.Text}.", "Success");
                });

                // update the "Forge to search in" combobox in the Find dialog
                if (findDialog != null)
                    findDialog.AddOrRemoveForge(node.Text);
            }
            else if (node.Type == EntryTreeNodeType.ENTRY) // populate with subentries (resource types)
            {
                List<EntryTreeNode> nodes = new List<EntryTreeNode>();
                treeView.Enabled = false; // prevent user-induced damages

                // work in the background
                Helpers.DoBackgroundWork(() =>
                {
                    nodes = PopulateEntry(node);
                }, () =>
                {
                    // prevent a crash
                    if (nodes == null)
                    {
                        treeView.Enabled = true;
                        return;
                    }

                    node.Nodes.AddRange(nodes.ToArray());
                    node.Nodes[0].Remove(); // remove the placeholder node

                    treeView.Enabled = true;
                    MessageBox.Show($"Loaded the subentries from {node.Text}.", "Success");
                });
            }
        }

        private void treeView_AfterCollapse(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null)
                return;
            EntryTreeNode node = (EntryTreeNode)e.Node;

            BeginMarquee();

            if (node.Type == EntryTreeNodeType.FORGE)
            {
                node.Nodes.Clear(); // remove the entry nodes
                treeView.Refresh();
                node.Nodes.Add(new EntryTreeNode()); // add a placeholder child node

                // update the "Forge to search in" combobox in the Find dialog
                if (findDialog != null)
                    findDialog.AddOrRemoveForge(node.Text);
            }
            else if (node.Type == EntryTreeNodeType.ENTRY)
            {
                node.Nodes.Clear(); // remove the subentry nodes
                treeView.Refresh();
                node.Nodes.Add(new EntryTreeNode()); // add a placeholder child node
            }

            EndMarquee();
        }

        private void treeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null)
                return;
            EntryTreeNode node = (EntryTreeNode)e.Node;

            // clear the 3D/Image/Text Viewers
            ClearTheViewers();

            Console.WriteLine("Resource Type: " + node.ResourceType);

            if (node.Type == EntryTreeNodeType.SUBENTRY) // forge subentry
                HandleSubentryNode(node);
            else if (node.Type == EntryTreeNodeType.IMAGE) // image file
                HandleImageNode(node);
            else if (node.Type == EntryTreeNodeType.PCK) // soundpack
                HandlePCKNode(node);
            else if (node.Type == EntryTreeNodeType.TEXT) // text file
                HandleTextNode(node);

            // focus on the treeview and select again the node
            treeView.Focus();
            treeView.SelectedNode = node;

            // update the path status label
            pathStatusLabel.Text = node.Path;

            // update size status label
            if (node.Size > -1)
                sizeStatusLabel.Text = $"Size: {Helpers.BytesToString(node.Size)}";
        }

        private void treeView_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                EntryTreeNode node = (EntryTreeNode)treeView.GetNodeAt(e.Location);
                EntryTreeNode sNode = (EntryTreeNode)treeView.SelectedNode;
                if (node == null || sNode == null)
                {
                    treeNodeContextMenuStrip.Close();
                    return;
                }

                // if the user right-clicked on a game folder node, close the context menu
                if (node == odysseyNode || node == originsNode || node == steepNode ||
                    sNode == odysseyNode || sNode == originsNode || sNode == steepNode)
                {
                    treeNodeContextMenuStrip.Close();
                }

                if ((node.Type == EntryTreeNodeType.FORGE && node.Nodes.Count > 1) || (sNode.Type == EntryTreeNodeType.FORGE && sNode.Nodes.Count > 1)) // forge
                {
                    UpdateContextMenu(enableForge: true);
                }
                else // forge entry or subentry
                {
                    if (node.Type == EntryTreeNodeType.ENTRY || sNode.Type == EntryTreeNodeType.ENTRY) // forge entry
                    {
                        UpdateContextMenu(enableDatafile: true);
                    }
                    else if (node.Type == EntryTreeNodeType.SUBENTRY || sNode.Type == EntryTreeNodeType.SUBENTRY) // forge subentry
                    {
                        if (node.ResourceType == ResourceType.BUILD_TABLE || sNode.ResourceType == ResourceType.BUILD_TABLE)
                            UpdateContextMenu(enableBuildTable: true);
                        else if (node.ResourceType == ResourceType.MESH || sNode.ResourceType == ResourceType.MESH)
                            UpdateContextMenu(enableModel: true);
                        else if (node.ResourceType == ResourceType.TEXTURE_MAP || sNode.ResourceType == ResourceType.TEXTURE_MAP)
                            UpdateContextMenu(enableTexture: true);
                        else
                            UpdateContextMenu();
                    }
                }
            }
        }
        
        private void resetCameraStripButton_Click(object sender, EventArgs e)
        {
            gl.ResetCamera();
        }
        #endregion

        #region Context menu
        private void copyNameToClipboardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;

            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode;
            Clipboard.SetText(node.Text);

            MessageBox.Show("Copied the name to the clipboard.", "Success");
        }

        // Build Table
        private void extractAllEntriesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;

            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode.Parent;

        }
        // end Build Table

        // Datafile
        private void saveRawDataAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;

            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode;
            Forge forge = node.GetForge();
            saveFileDialog.FileName = $"{node.Text}.dat";
            saveFileDialog.Filter = "Raw Data|*.dat|All Files|*.*";
            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                Forge.FileEntry entry = forge.GetFileEntry(node.Text);
                byte[] data = forge.GetRawData(entry);
                try
                {
                    File.WriteAllBytes(saveFileDialog.FileName, data);
                }
                catch (IOException ee)
                {
                    Console.WriteLine(ee);
                }
                finally
                {
                    MessageBox.Show("Extracted decompressed data.", "Success");
                }
            }
        }

        private void saveDecompressedDataAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;

            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode;
            Forge forge = node.GetForge();
            string text = node.Text;
            byte[] decompressedData = null;

            BeginMarquee();

            Helpers.DoBackgroundWork(() =>
            {
                Forge.FileEntry entry = forge.GetFileEntry(text);
                byte[] rawData = forge.GetRawData(entry);
                decompressedData = Odyssey.ReadFile(rawData);
            }, () =>
            {
                EndMarquee();

                // failure
                if (decompressedData.Length == 0 || decompressedData == null)
                {
                    MessageBox.Show("Could not decompress data.", "Failure");
                    return;
                }

                saveFileDialog.FileName = string.Concat(node.Text, ".", Helpers.GameToExtension(node.Game));
                saveFileDialog.Filter = $"{Helpers.NameOfGame(node.Game)} Data|*.{Helpers.GameToExtension(node.Game)}|All Files|*.*";
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllBytes(saveFileDialog.FileName, decompressedData);
                    }
                    catch (IOException ee)
                    {
                        Console.WriteLine(ee);
                    }
                    finally
                    {
                        MessageBox.Show("Saved compressed data.", "Success");
                    }
                }
            });
        }
        
        private void showResourceViewerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;

            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode;
            if (node.Nodes.Count == 0)
                return;

            ResourceViewer viewer = new ResourceViewer();
            viewer.LoadNode(node);
            viewer.Show();
        }
        // end Datafile

        // Forge
        private void createFilelistToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;

            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode;
            Forge forge = node.GetForge();

            if (forge != null)
            {
                string filelist = forge.CreateFilelist();
                if (filelist.Length > 0)
                {
                    using (SaveFileDialog dialog = new SaveFileDialog())
                    {
                        dialog.FileName = $"{forge.Name}-filelist.txt";
                        dialog.Filter = "Text Files|*.txt|All Files|*.*";
                        if (dialog.ShowDialog() == DialogResult.OK)
                        {
                            File.WriteAllText(dialog.FileName, filelist);
                            MessageBox.Show("Created filelist.", "Success");
                        }
                    }
                }
            }
        }

        private void extractAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;
            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode;

            Forge forge = node.GetForge();
            if (forge != null && forge.FileEntries.Length > 0)
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        BeginMarquee();
                        Parallel.ForEach(forge.FileEntries, (fe) =>
                        {
                            string name = fe.NameTable.Name;
                            byte[] data = forge.GetRawData(fe);
                            File.WriteAllBytes(Path.Combine(dialog.SelectedPath, name), data);
                        });
                        EndMarquee();
                        MessageBox.Show($"Extracted all of {forge.Name}.", "Success");
                    }
                }
            }
        }
        // end Forge

        // Model
        private void saveAsOBJToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null || meshes == null)
                return;
            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode.Parent;

            saveFileDialog.FileName = node.Text;
            saveFileDialog.Filter = Helpers.MODEL_CONVERSION_FORMATS;
            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                for (int i = 0; i < meshes.Count; i++)
                {
                    string fileName = string.Concat(Path.Combine(Path.GetDirectoryName(saveFileDialog.FileName), Path.GetFileNameWithoutExtension(saveFileDialog.FileName)), "-", i, Path.GetExtension(saveFileDialog.FileName)); // all this to add a suffix
                    if (Path.GetExtension(saveFileDialog.FileName) == ".dae")
                    {
                        IO_DAE.ExportModelAsDAE(fileName, meshes, false, false);
                    }
                    else if(Path.GetExtension(saveFileDialog.FileName) == ".obj")
                    {
                        File.WriteAllText(fileName, meshes[i].OBJData);
                    }
                }
                MessageBox.Show("Extracted all meshes as separate files.", "Success");
            }
        }
        // end Model

        // Texture
        private void convertToAnotherFormatToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;
            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode.Parent;

            string text = node.Text;
            string tex = "";

            if (File.Exists($"{Helpers.GetTempPath(text)}.dds"))
                tex = $"{Helpers.GetTempPath(text)}.dds";
            else if (File.Exists($"{Helpers.GetTempPath(text)}_TopMip_0.dds"))
                tex = $"{Helpers.GetTempPath(text)}_TopMip_0.dds";
            else if (File.Exists($"{Helpers.GetTempPath(text)}_Mip0.dds"))
                tex = $"{Helpers.GetTempPath(text)}_Mip0.dds";

            if (!string.IsNullOrEmpty(tex))
            {
                saveFileDialog.FileName = Path.GetFileNameWithoutExtension(tex);
                saveFileDialog.Filter = Helpers.TEXTURE_CONVERSION_FORMATS;
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    Helpers.ConvertDDS(tex, Path.GetDirectoryName(saveFileDialog.FileName), () =>
                    {
                        MessageBox.Show("Converted the texture.", "Success");
                    }, Path.GetExtension(saveFileDialog.FileName).Substring(1));
                }
            }
        }

        // simply copy the file
        private void saveAsDDSToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeView.SelectedNode == null)
                return;
            EntryTreeNode node = (EntryTreeNode)treeView.SelectedNode.Parent;

            string text = node.Text;
            string dds = "";

            if (File.Exists($"{Helpers.GetTempPath(text)}.dds"))
                dds = $"{Helpers.GetTempPath(text)}.dds";
            else if (File.Exists($"{Helpers.GetTempPath(text)}_TopMip_0.dds"))
                dds = $"{Helpers.GetTempPath(text)}_TopMip_0.dds";
            else if (File.Exists($"{Helpers.GetTempPath(text)}_Mip0.dds"))
                dds = $"{Helpers.GetTempPath(text)}_Mip0.dds";

            if (!string.IsNullOrEmpty(dds))
            {
                saveFileDialog.FileName = Path.GetFileName(dds);
                saveFileDialog.Filter = "DirectDraw Surface|*.dds|All Files|*.*";
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    File.Copy(dds, saveFileDialog.FileName);
                    MessageBox.Show("Saved the texture.", "Success");
                }
            }
        }
        // end Texture
        #endregion

        #region Menus
        #region File
        private void quitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }
        #endregion
        #region Find
        private void findToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (Helpers.GetOpenForms().Where((f) => f.Text == "Find").Count() > 0 && findDialog != null)
                findDialog.BringToFront();
            else
            {
                findDialog = new FindDialog();
                findDialog.FindAll += OnFindAll;
                findDialog.ShowInList += OnShowInList;
                findDialog.Show();
            }
        }
        #endregion
        #region Tools
        private void decompressFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                saveFileDialog.Filter = "Decompressed Data|*.dec|All Files|*.*";
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    using (Stream stream = new FileStream(openFileDialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        using (BinaryReader reader = new BinaryReader(stream))
                        {
                            if (Helpers.LocateRawDataIdentifier(reader).Length < 2)
                            {
                                MessageBox.Show("Operation failed, due to improper data.", "Failure");
                                return;
                            }

                            BeginMarquee();

                            long secondRawData = Helpers.LocateRawDataIdentifier(reader)[1];
                            reader.BaseStream.Seek(10, SeekOrigin.Current); // ignore everything until the compression byte
                            byte compression = reader.ReadByte();

                            bool success = false;

                            Helpers.DoBackgroundWork(() =>
                            {
                                if (secondRawData > 0)
                                {
                                    if (compression == 0x08)
                                        success = Odyssey.ReadFile(openFileDialog.FileName, false, saveFileDialog.FileName);
                                    else if (compression == 0x05)
                                        success = Steep.ReadFile(openFileDialog.FileName, false, saveFileDialog.FileName);
                                }
                            }, () =>
                            {
                                EndMarquee();
                                if (success)
                                    MessageBox.Show("Decompressed file successfully. Check the folder where the compressed file is located.", "Success");
                                else
                                    MessageBox.Show("Unknown compression type.", "Failure");
                            });
                        }
                    }
                }
            }
        }

        private void showFileInTheViewersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            openFileDialog.Filter = Helpers.DECOMPRESSED_FILE_FORMATS;
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                Console.WriteLine($"Selected file: {openFileDialog.FileName}");
                ResourceType type = Helpers.GetFirstResourceType(openFileDialog.FileName);
                Console.WriteLine($"Selected file resource type: {type}");
                Game game = Helpers.ExtensionToGame(Path.GetExtension(openFileDialog.FileName).Substring(1));
                Console.WriteLine($"Selected file game: {game}");

                BeginMarquee();
                ClearTheViewers(true);

                Helpers.DoBackgroundWork(() =>
                {
                    if (game == Game.ORIGINS && type == ResourceType.MESH)
                    {
                        meshes = Origins.ExtractModel(openFileDialog.FileName, (string text) =>
                        {
                            Invoke(new Action(() =>
                            {
                                tabControl.SelectedIndex = 0;
                                richTextBox.Text = text;
                            }));
                        });
                    }
                    else
                    {
                        MessageBox.Show("Only Origins models can be viewed using this tool for now.", "Failure");
                    }
                    // ToDo: implement texture map
                }, () =>
                {
                    EndMarquee();

                    if (type == ResourceType.MESH)
                    {
                        if (meshes == null)
                            return;

                        List<Model> models = new List<Model>();
                        foreach (Mesh mesh in meshes)
                        {
                            OBJModel m = OBJModel.LoadFromString(mesh.OBJData);
                            m.Scale = new Vector3(.001f, .001f, .001f);
                            m.CalculateNormals();
                            models.Add(m);
                            gl.Models.Add(m);
                        }

                        if (meshes != null && meshes.Count > 0)
                        {
                            Vector3 center = models.First().GetCenterOfAABB(models.First().CalculateAABB());
                            gl.SetCameraResetPosition(Vector3.Add(center, new Vector3(0, 0, 10)));
                            gl.ResetCamera();
                        }
                    }
                    // ToDo: implement texture map
                });
            }
        }

        private void decompileLocalizationDataToolStripMenuItem_Click(object sender, EventArgs e)
        {
        }
        #endregion
        #region Settings
        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Settings settings = new Settings();
            // refresh the tree view when the Settings window is about to close
            settings.FormClosing += new FormClosingEventHandler((object o, FormClosingEventArgs args) =>
            {
                // ToDo: add Properties.Settings event handler here, so that I can detect when a game path was changed

                // reload games in the tree view
                LoadGamesIntoTreeView();

                // update settings
                LoadSettings();
            });
            settings.ShowDialog();
        }
        #endregion
        #region More
        private void sourceCodeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("https://github.com/theawesomecoder61/Blacksmith");
        }

        private void helpToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("https://github.com/theawesomecoder61/Blacksmith/wiki/Help");
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new AboutBox().ShowDialog();
        }
        #endregion
        #endregion

        #region Find events
        private void OnFindAll(object sender, FindEventArgs args)
        {
            List<EntryTreeNode> nodeResults = new List<EntryTreeNode>();
            nodeToSearchIn = odysseyNode.Nodes.Cast<EntryTreeNode>().ToArray().Where(x => x.IsExpanded && x.Type == EntryTreeNodeType.FORGE).FirstOrDefault() ??
                originsNode.Nodes.Cast<EntryTreeNode>().ToArray().Where(x => x.IsExpanded && x.Type == EntryTreeNodeType.FORGE).FirstOrDefault() ??
                steepNode.Nodes.Cast<EntryTreeNode>().ToArray().Where(x => x.IsExpanded && x.Type == EntryTreeNodeType.FORGE).FirstOrDefault();
            if (nodeToSearchIn == null)
                return;

            Console.WriteLine("Finding within: " + nodeToSearchIn.Text);
            string[] nameResults = null;

            Helpers.DoBackgroundWork(() =>
            {
                Invoke(new Action(() =>
                {
                    try
                    {
                        nameResults = ((string[])nodeToSearchIn.Tag).Where(x =>
                            (args.Type == FindType.NORMAL && args.CaseSensitive && x.Contains(args.Query)) ||
                            (args.Type == FindType.NORMAL && !args.CaseSensitive && x.ToLower().Contains(args.Query.ToLower())) ||
                            (args.Type == FindType.REGEX && args.CaseSensitive && Regex.IsMatch(x, args.Query)) ||
                            (args.Type == FindType.REGEX && !args.CaseSensitive && Regex.IsMatch(x.ToLower(), args.Query.ToLower())) ||
                            (args.Type == FindType.WILDCARD && args.CaseSensitive && Regex.IsMatch(x, Helpers.WildcardToRegEx(args.Query))) ||
                            (args.Type == FindType.WILDCARD && !args.CaseSensitive && Regex.IsMatch(x.ToLower(), Helpers.WildcardToRegEx(args.Query.ToLower())))
                        ).ToArray();
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show(e.Message, "Failure");
                    }
                    finally
                    {
                        if (nameResults != null)
                        {
                            foreach (EntryTreeNode n1 in nodeToSearchIn.Nodes)
                            {
                                foreach (string n2 in nameResults)
                                {
                                    if (n1.Text.Equals(n2))
                                        nodeResults.Add(n1);
                                }
                            }
                        }
                    }
                }));
            }, () =>
            {
                Console.WriteLine("Results: " + nodeResults.Count);
                findDialog.LoadResults(nodeResults);
            });
        }

        private void OnShowInList(object sender, ShowInListArgs args)
        {
            BringToFront();
            Focus();

            if (nodeToSearchIn == null)
                return;

            TreeNode[] results = nodeToSearchIn.Nodes.Cast<TreeNode>().Where(x => x.Text.Equals(args.Name, StringComparison.InvariantCultureIgnoreCase)).ToArray();
            Console.WriteLine(">> Query: " + args.Name);
            Console.WriteLine(">> Results: " + results.Length);
            if (results == null || results.Length == 0)
                return;

            treeView.SelectedNode = results[0];
            results[0].EnsureVisible();
        }
        #endregion

        #region Helpers
        private void HandlePCKNode(EntryTreeNode node)
        {
            SoundpackBrowser browser = new SoundpackBrowser();
            browser.LoadPack(node.Path);
            browser.Show();
        }

        private void HandleImageNode(EntryTreeNode node)
        {
            currentImgPath = node.Path;
            pictureBox.Image = Image.FromFile(currentImgPath);
            tabControl.SelectedIndex = 1;
        }

        private void HandleTextNode(EntryTreeNode node)
        {
            richTextBox.Text = File.ReadAllText(node.Path);
            tabControl.SelectedIndex = 2;
        }

        private void HandleSubentryNode(EntryTreeNode node)
        {
            ClearTheViewers(true);

            switch (node.ResourceType)
            {
                case ResourceType.MESH: // meshes/models
                    switch (node.Game)
                    {
                        case Game.ODYSSEY:
                            // ToDo: to be implemented
                            break;
                        case Game.ORIGINS:
                            meshes = Origins.ExtractModel(Helpers.GetTempPath(node.Path), (string text) =>
                            {
                                tabControl.SelectedIndex = 0;
                                richTextBox.Text = text;
                            });

                            if (meshes == null)
                                return;

                            List<Model> models = new List<Model>();
                            foreach (Mesh mesh in meshes)
                            {
                                OBJModel m = OBJModel.LoadFromString(mesh.OBJData);
                                m.Scale = new Vector3(.001f, .001f, .001f);
                                m.CalculateNormals();
                                models.Add(m);
                                gl.Models.Add(m);
                            }

                            if (meshes != null && meshes.Count > 0)
                            {
                                Vector3 center = models.First().GetCenterOfAABB(models.First().CalculateAABB());
                                gl.SetCameraResetPosition(Vector3.Add(center, new Vector3(0, 0, 10)));
                                gl.ResetCamera();
                            }
                            break;
                        case Game.STEEP:
                            // ToDo: to be implemented
                            break;
                    }
                    break;
                case ResourceType.TEXTURE_MAP: // texture maps
                    if (node.Game == Game.ODYSSEY || node.Game == Game.ORIGINS)
                    {
                        Odyssey.ExtractTextureMap(Helpers.GetTempPath(node.Path), node, (string path) =>
                        {
                            HandleTextureMap(path);
                        });
                    }
                    else if (node.Game == Game.STEEP)
                    {
                        Steep.ExtractTextureMap(Helpers.GetTempPath(node.Path), node, (string path) =>
                        {
                            HandleTextureMap(path);
                        });
                    }
                    break;
            } 
        }

        private void HandleTextureMap(string texturePath)
        {
            if (texturePath == "FAILED")
            {
                MessageBox.Show("Texture failed to convert to PNG. Alternatively, you can right-click the TEXTURE_MAP node, select \"Texture\">\"Save As DDS\".", "Failure");
                return;
            }

            currentImgPath = texturePath;
            Console.WriteLine(">> CURRENT IMAGE PATH: " + currentImgPath);
            if (!Disposing || !IsDisposed)
            {
                Invoke(new Action(() =>
                {
                    UpdatePictureBox(Image.FromFile(currentImgPath));
                    zoomDropDownButton.Text = "Zoom Level: 100%";
                    tabControl.SelectedIndex = 1;
                }));
            }
        }

        private void LoadGamesIntoTreeView()
        {
            treeView.Nodes.Clear();

            // Odyssey
            odysseyNode = new EntryTreeNode
            {
                Game = Game.ODYSSEY,
                Text = "Assassin's Creed: Odyssey"                
            };
            treeView.Nodes.Add(odysseyNode);
            if (!string.IsNullOrEmpty(Properties.Settings.Default.odysseyPath))
            {
                odysseyNode.Path = Properties.Settings.Default.odysseyPath;
                PopulateTreeView(Properties.Settings.Default.odysseyPath, odysseyNode);
            }

            // Origins
            originsNode = new EntryTreeNode
            {
                Game = Game.ORIGINS,
                Text = "Assassin's Creed: Origins"
            };
            treeView.Nodes.Add(originsNode);
            if (!string.IsNullOrEmpty(Properties.Settings.Default.originsPath))
            {
                originsNode.Path = Properties.Settings.Default.originsPath;
                PopulateTreeView(Properties.Settings.Default.originsPath, originsNode);
            }

            // Steep
            steepNode = new EntryTreeNode
            {
                Game = Game.STEEP,
                Text = "Steep"
            };
            treeView.Nodes.Add(steepNode);
            if (!string.IsNullOrEmpty(Properties.Settings.Default.steepPath))
            {
                steepNode.Path = Properties.Settings.Default.steepPath;
                PopulateTreeView(Properties.Settings.Default.steepPath, steepNode);
            }
        }
        
        private void LoadSettings()
        {
            gl.BackgroundColor = Properties.Settings.Default.threeBG;
            gl.RenderMode = Properties.Settings.Default.renderMode == 0 ? RenderMode.SOLID : Properties.Settings.Default.renderMode == 1 ? RenderMode.WIREFRAME : RenderMode.POINTS;
            gl.PointSize = Properties.Settings.Default.pointSize;

            // create a temporary directory, if the user forgot
            if (string.IsNullOrEmpty(Properties.Settings.Default.tempPath))
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Blacksmith");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    Properties.Settings.Default.tempPath = dir;
                }
            }
        }
        
        private void PopulateTreeView(string dir, EntryTreeNode parent)
        {
            foreach (string file in Directory.GetFileSystemEntries(dir))
            {
                FileInfo info = new FileInfo(file);
                EntryTreeNode node = new EntryTreeNode
                {
                    Game = parent.Game,
                    Path = Path.Combine(dir, file),
                    Size = Directory.Exists(file) ? 0 : info.Length, // directories have no size
                    Text = Path.GetFileName(file)                    
                };

                if (Directory.Exists(file) || Helpers.IsSupportedFile(Path.GetExtension(file)))
                {
                    // deal with each supported file type
                    if (Helpers.IsSupportedFile(Path.GetExtension(file)))
                    {
                        if (Path.GetExtension(file).Equals(".forge")) // forge
                        {
                            node.Forge = new Forge(file);
                            node.Type = EntryTreeNodeType.FORGE;
                            node.Nodes.Add(new EntryTreeNode()); // used as a placeholder, will be removed later
                        }
                        else if (Path.GetExtension(file).Equals(".pck")) // pck
                            node.Type = EntryTreeNodeType.PCK;
                        else if (Path.GetExtension(file).Equals(".png")) // image
                            node.Type = EntryTreeNodeType.IMAGE;
                        else if (Regex.Matches(file, @"(.txt|.ini|.log)").Count > 0) // text
                            node.Type = EntryTreeNodeType.TEXT;
                    }

                    if (parent != null)
                        parent.Nodes.Add(node);
                    else
                        treeView.Nodes.Add(node);
                }

                // recursively call this function (for directories)
                if (Directory.Exists(file))
                {
                    node.Type = EntryTreeNodeType.DIRECTORY;
                    PopulateTreeView(file, node);
                }
            }
        }
        
        private List<EntryTreeNode> PopulateForge(EntryTreeNode node)
        {
            Forge forge = node.Forge;
            if (forge == null)
                return null;

            // read if only the forge has not been read
            if (!forge.IsFullyRead())
            {
                if (forge.GetEntryCount() > 20000)
                {
                    string entries = string.Format("{0:n0}", forge.GetEntryCount());
                    if (MessageBox.Show($"This .forge contains more than 20,000 entries ({entries} exactly). Blacksmith may freeze while loading or it may not load them at all.\nDo this at your own risk.", "Warning", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        forge.Read();
                    else
                        return null;
                }
                else
                    forge.Read();
                node.Forge = forge; // set again for assurance
            }

            // populate the forge tree with its entries
            List<EntryTreeNode> entryNodes = new List<EntryTreeNode>();
            foreach (Forge.FileEntry entry in forge.FileEntries)
            {
                EntryTreeNode n = new EntryTreeNode
                {
                    Game = node.Game,
                    Offset = entry.IndexTable.OffsetToRawDataTable,
                    Path = Path.Combine(node.Path, entry.NameTable.Name),
                    Size = entry.IndexTable.RawDataSize,
                    Text = entry.NameTable.Name,
                    Type = EntryTreeNodeType.ENTRY
                };

                n.Nodes.Add(new EntryTreeNode(""));
                entryNodes.Add(n);
            }

            return entryNodes;
        }

        private List<EntryTreeNode> PopulateEntry(EntryTreeNode node)
        {
            // extract the contents from the forge
            Forge forge = node.GetForge();
            byte[] data = forge.GetRawData(node.Offset, node.Size);

            // decompress
            string file = $"{node.Text}.{Helpers.GameToExtension(node.Game)}";
            if (node.Game == Game.ODYSSEY)
                Helpers.WriteToFile(file, Odyssey.ReadFile(data), true);
            else if (node.Game == Game.ORIGINS)
                Helpers.WriteToFile(file, Odyssey.ReadFile(data), true);
            else if (node.Game == Game.STEEP)
                Helpers.WriteToFile(file, Steep.ReadFile(data), true);

            // path will hold the file name
            node.Path = file;

            // get resource locations and create nodes
            List<EntryTreeNode> nodes = new List<EntryTreeNode>();
            using (Stream stream = new FileStream(Helpers.GetTempPath(file), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    Helpers.ResourceLocation[] locations = Helpers.LocateResourceIdentifiers(reader);
                    foreach (Helpers.ResourceLocation location in locations)
                    {
                        if (Helpers.IMPORTANT_RESOURCE_TYPES.Contains(location.Type))
                        {
                            nodes.Add(new EntryTreeNode
                            {
                                Game = node.Game,
                                ImageIndex = GetImageIndex(location.Type),
                                Path = file,
                                ResourceOffset = location.Offset,
                                ResourceType = location.Type,
                                Text = location.Type.ToString().Replace("_", ""),
                                Type = EntryTreeNodeType.SUBENTRY
                            });
                        }
                    }
                }
            }

            return nodes;
        }

        private void ClearTheViewers(bool clear3D = false)
        {
            if (clear3D)
                gl.Models.Clear();
            pictureBox.Image = null;
            richTextBox.Text = "";
        }

        private void BeginMarquee()
        {
            toolStripProgressBar.Visible = true;
            toolStripProgressBar.Style = ProgressBarStyle.Marquee;
        }
        
        private void EndMarquee()
        {
            toolStripProgressBar.Style = ProgressBarStyle.Blocks;
            toolStripProgressBar.Visible = false;
        }
        
        private void UpdateContextMenu(bool enableBuildTable = false, bool enableDatafile = false, bool enableForge = false, bool enableModel = false, bool enableTexture = false)
        {
            buildTableToolStripMenuItem.Visible = enableBuildTable;
            datafileToolStripMenuItem.Visible = enableDatafile;
            forgeToolStripMenuItem.Visible = enableForge;
            modelToolStripMenuItem.Visible = enableModel;
            textureToolStripMenuItem.Visible = enableTexture;
        }
        
        private void ChangeZoom(double zoom)
        {
            if (!string.IsNullOrEmpty(currentImgPath))
            {
                UpdatePictureBox(Helpers.ZoomImage(Image.FromFile(currentImgPath), zoom), true, false);
                pictureBox.Size = pictureBox.Image.Size;
                zoomDropDownButton.Text = $"Zoom Level: {zoom * 100}%";
            }
        }
        
        private void UpdatePictureBox(Image image, bool reload = true, bool flip = true, bool isZoomed = false)
        {
            if (image.Height <= pictureBox.ClientSize.Height && !isZoomed)
            {
                pictureBox.SizeMode = PictureBoxSizeMode.CenterImage;
                pictureBox.Dock = DockStyle.Fill;
            }
            else
            {
                pictureBox.SizeMode = PictureBoxSizeMode.AutoSize;
                pictureBox.Dock = DockStyle.None;
            }

            // reload, if true, causes the image to refresh and dimensions label to update
            if (!reload)
                return;

            // rotate
            if (flip)
                image.RotateFlip(RotateFlipType.Rotate180FlipX);

            pictureBox.Image = image;
            pictureBox.Refresh();
            imageDimensStatusLabel.Text = $"Dimensions: {image.Width}x{image.Height}";
        }

        private int GetImageIndex(ResourceType type)
        {
            if (type == ResourceType.COMRPESSED_LOCALIZATION_DATA)
                return 25;
            else if (type == ResourceType.LOCALIZATION_MANAGER)
                return 6;
            else if (type == ResourceType.LOCALIZATION_PACKAGE)
                return 15;
            else if (type == ResourceType.MATERIAL)
                return 16;
            else if (type == ResourceType.MESH)
                return 4;
            else if (type == ResourceType.MIPMAP)
                return 17;
            else if (type == ResourceType.TEXTURE_MAP)
                return 13;
            else
                return 0;
        }

        private void toggleAlphaButton_Click(object sender, EventArgs e)
        {
            useAlpha = !useAlpha;
            if (useAlpha)
                imagePanel.BackgroundImage = Properties.Resources.grid;
            else
                imagePanel.BackgroundImage = null;
        }
        #endregion
    }
}