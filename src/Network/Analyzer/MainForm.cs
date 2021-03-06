﻿// <copyright file="MainForm.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Network.Analyzer
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.ComponentModel.Design;
    using System.Diagnostics;
    using System.Linq;
    using System.Linq.Dynamic.Core;
    using System.Linq.Expressions;
    using System.Text;
    using System.Windows.Forms;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;
    using MUnique.OpenMU.Network.PlugIns;
    using MUnique.OpenMU.PlugIns;
    using Zuby.ADGV;

    /// <summary>
    /// The main form of the analyzer.
    /// </summary>
    public partial class MainForm : Form
    {
        private readonly BindingList<ICapturedConnection> proxiedConnections = new ();

        private readonly Dictionary<ClientVersion, string> clientVersions = new ()
        {
            { new ClientVersion(6, 3, ClientLanguage.English), "S6E3 (1.04d)" },
            { new ClientVersion(1, 0, ClientLanguage.Invariant), "Season 1 - 6" },
            { new ClientVersion(0, 97, ClientLanguage.Invariant), "0.97" },
            { new ClientVersion(0, 75, ClientLanguage.Invariant), "0.75" },
        };

        private readonly PacketAnalyzer analyzer;

        private readonly PlugInManager plugInManager;

        private LiveConnectionListener? clientListener;
        private BindingList<Packet>? unfilteredList;
        private Delegate? filterMethod;
        private LambdaExpression? filterExpression;

        /// <summary>
        /// Initializes a new instance of the <see cref="MainForm"/> class.
        /// </summary>
        public MainForm()
        {
            this.InitializeComponent();
            var serviceContainer = new ServiceContainer();
            serviceContainer.AddService(typeof(ILoggerFactory), new NullLoggerFactory());
            this.plugInManager = new PlugInManager(null, new NullLoggerFactory(), serviceContainer);
            this.plugInManager.DiscoverAndRegisterPlugIns();
            this.clientBindingSource.DataSource = this.proxiedConnections;
            this.connectedClientsListBox.DisplayMember = nameof(ICapturedConnection.Name);
            this.connectedClientsListBox.Update();

            this.analyzer = new PacketAnalyzer();
            this.Disposed += (_, _) => this.analyzer.Dispose();

            this.clientVersionComboBox.SelectedIndexChanged += (_, _) =>
            {
                if (this.clientListener is { } listener)
                {
                    listener.ClientVersion = this.SelectedClientVersion;
                }

                this.analyzer.ClientVersion = this.SelectedClientVersion;
            };
            this.clientVersionComboBox.DataSource = new BindingSource(this.clientVersions, null);
            this.clientVersionComboBox.DisplayMember = "Value";
            this.clientVersionComboBox.ValueMember = "Key";

            this.targetHostTextBox.TextChanged += (_, _) =>
            {
                var listener = this.clientListener;
                if (listener != null)
                {
                    listener.TargetHost = this.targetHostTextBox.Text;
                }
            };
            this.targetPortNumericUpDown.ValueChanged += (_, _) =>
            {
                var listener = this.clientListener;
                if (listener != null)
                {
                    listener.TargetPort = (int)this.targetPortNumericUpDown.Value;
                }
            };

            this.packetGridView.FilterStringChanged += this.OnPacketFilterStringChanged;
            foreach (DataGridViewColumn column in this.packetGridView.Columns)
            {
                this.packetGridView.SetSortEnabled(column, false);
            }
        }

        private ClientVersion SelectedClientVersion
        {
            get
            {
                if (this.clientVersionComboBox.SelectedItem is KeyValuePair<ClientVersion, string> selectedItem)
                {
                    return selectedItem.Key;
                }

                return default;
            }
        }

        private ICapturedConnection? SelectedConnection
        {
            get
            {
                var index = this.connectedClientsListBox.SelectedIndex;
                if (index < 0)
                {
                    return null;
                }

                return this.proxiedConnections[index];
            }
        }

        /// <inheritdoc />
        protected override void OnClosed(EventArgs e)
        {
            if (this.clientListener != null)
            {
                this.clientListener.Stop();
                this.clientListener = null;
            }

            base.OnClosed(e);
        }

        private static string ConvertFilterStringToExpressionString(string filter)
        {
            var result = new StringBuilder();

            filter = filter.Replace("(", string.Empty).Replace(")", string.Empty);

            var andOperator = string.Empty;
            foreach (var columnFilter in filter.Split("AND"))
            {
                // Example: [Type] IN 'C1', 'C2'
                result.Append(andOperator);

                var temp1 = columnFilter.Trim().Split("IN");
                var columnName = temp1[0].Split('[', ']')[1].Trim();

                // prepare beginning of linq statement
                result.Append("(")
                    .Append(columnName)
                    .Append(" != null && (");

                string orOperator = string.Empty;

                var filterValues = temp1[1].Split(',').Select(v => v.Replace("\'", string.Empty).Trim());

                foreach (var filterValue in filterValues)
                {
                    result.Append(orOperator).Append(columnName);
                    if (double.TryParse(filterValue, out _))
                    {
                        result.Append(" = ").Append(filterValue);
                    }
                    else
                    {
                        result.Append(".Contains(\"").Append(filterValue).Append("\")");
                    }

                    orOperator = " OR ";
                }

                result.Append("))");

                andOperator = " AND ";
            }

            // replace all single quotes with double quotes
            return result.ToString();
        }

        private void UpdateFilterExpression()
        {
            var filterString = this.packetGridView.FilterString;
            if (string.IsNullOrEmpty(filterString))
            {
                this.filterExpression = null;
                this.filterMethod = null;
            }
            else
            {
                var filterExpressionString = ConvertFilterStringToExpressionString(filterString);
                this.filterExpression = DynamicExpressionParser.ParseLambda(ParsingConfig.Default, true, typeof(Packet), typeof(bool), filterExpressionString);
                this.filterMethod = this.filterExpression.Compile(false);
            }
        }

        private void SetPacketDataSource()
        {
            if (this.unfilteredList is { } oldList)
            {
                oldList.ListChanged -= this.OnUnfilteredListChanged;
            }

            this.unfilteredList = this.SelectedConnection?.PacketList;

            if (this.unfilteredList is null)
            {
                return;
            }

            if (this.filterExpression is null)
            {
                this.packetBindingSource.DataSource = this.SelectedConnection?.PacketList;
            }
            else
            {
                this.packetBindingSource.DataSource = this.unfilteredList.AsQueryable().Where(this.filterExpression).ToList();
                this.unfilteredList.ListChanged += this.OnUnfilteredListChanged;
            }
        }

        private void OnUnfilteredListChanged(object sender, ListChangedEventArgs e)
        {
            if (e.ListChangedType != ListChangedType.ItemAdded
                || this.filterMethod is not { } filter
                || this.unfilteredList is not { } sourceList
                || this.unfilteredList?.Count < e.NewIndex
                || e.NewIndex < 0)
            {
                return;
            }

            var newPacket = sourceList[e.NewIndex];

            if (filter.DynamicInvoke(newPacket) is true)
            {
                this.packetBindingSource.Add(newPacket);
            }
        }

        private void OnPacketFilterStringChanged(object? sender, AdvancedDataGridView.FilterEventArgs e)
        {
            try
            {
                this.UpdateFilterExpression();
                this.SetPacketDataSource();
            }
            catch (Exception ex)
            {
                Debug.Fail(ex.Message, ex.StackTrace);
            }
        }

        private void StartProxy(object sender, System.EventArgs e)
        {
            if (this.clientListener != null)
            {
                this.clientListener.Stop();
                this.clientListener = null;
                this.btnStartProxy.Text = "Start Proxy";
                return;
            }

            this.clientListener = new LiveConnectionListener(
                (int)this.listenerPortNumericUpDown.Value,
                this.targetHostTextBox.Text,
                (int)this.targetPortNumericUpDown.Value,
                this.plugInManager,
                new NullLoggerFactory(),
                this.InvokeByProxy)
            {
                ClientVersion = this.SelectedClientVersion,
            };

            this.analyzer.ClientVersion = this.SelectedClientVersion;
            this.clientListener.ClientConnected += this.ClientListenerOnClientConnected;
            this.clientListener.Start();
            this.btnStartProxy.Text = "Stop Proxy";
        }

        private void ClientListenerOnClientConnected(object? sender, ClientConnectedEventArgs e)
        {
            this.InvokeByProxy(new Action(() =>
            {
                this.proxiedConnections.Add(e.Connection);
                if (this.proxiedConnections.Count == 1)
                {
                    this.connectedClientsListBox.SelectedItem = e.Connection;
                    this.OnConnectionSelected(this, EventArgs.Empty);
                }
            }));
        }

        private void InvokeByProxy(Delegate action)
        {
            if (this.Disposing || this.IsDisposed)
            {
                return;
            }

            try
            {
                this.Invoke(action);
            }
            catch (ObjectDisposedException)
            {
                // the application is probably just closing down... so swallow this error.
            }
        }

        private void OnPacketSelected(object sender, EventArgs e)
        {
            this.SuspendLayout();
            try
            {
                var rows = this.packetGridView.SelectedRows;
                if (rows.Count > 0 && this.packetGridView.SelectedRows[0].DataBoundItem is Packet packet)
                {
                    this.rawDataTextBox.Text = packet.PacketData;
                    this.extractedInfoTextBox.Text = this.analyzer.ExtractInformation(packet);
                    this.packetInfoGroup.Enabled = true;
                }
                else
                {
                    this.packetInfoGroup.Enabled = false;
                    this.rawDataTextBox.Text = string.Empty;
                    this.extractedInfoTextBox.Text = string.Empty;
                }
            }
            finally
            {
                this.ResumeLayout();
            }
        }

        private void OnConnectionSelected(object sender, EventArgs e)
        {
            this.SetPacketDataSource();
            if (this.SelectedConnection is { } connection)
            {
                this.trafficGroup.Enabled = true;
                this.trafficGroup.Text = $"Traffic ({connection.Name})";
            }
            else
            {
                this.trafficGroup.Enabled = false;
                this.trafficGroup.Text = "Traffic";
            }
        }

        private void OnDisconnectClientClick(object sender, EventArgs e)
        {
            var index = this.connectedClientsListBox.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            var proxy = this.proxiedConnections[index] as LiveConnection;
            proxy?.Disconnect();
        }

        private void OnLoadFromFileClick(object sender, EventArgs e)
        {
            using var loadFileDialog = new OpenFileDialog { Filter = "Analyzer files (*.mucap)|*.mucap" };
            if (loadFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                var loadedConnection = new SavedConnection(loadFileDialog.FileName);
                if (loadedConnection.PacketList.Count == 0)
                {
                    MessageBox.Show("The file couldn't be loaded. It was either empty or in a wrong format.", this.loadToolStripMenuItem.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    this.proxiedConnections.Add(loadedConnection);
                    this.connectedClientsListBox.SelectedItem = loadedConnection;
                    this.OnConnectionSelected(this, EventArgs.Empty);
                }
            }
        }

        private void OnSaveToFileClick(object sender, EventArgs e)
        {
            var index = this.connectedClientsListBox.SelectedIndex;
            if (index < 0 || this.proxiedConnections[index] is not { } capturedConnection)
            {
                return;
            }

            using var saveFileDialog = new SaveFileDialog
            {
                DefaultExt = "mucap",
                Filter = "Analyzer files (*.mucap)|*.mucap",
                AddExtension = true,
            };
            if (saveFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                capturedConnection.SaveToFile(saveFileDialog.FileName);
            }
        }

        private void OnSendPacketClick(object sender, EventArgs e)
        {
            var index = this.connectedClientsListBox.SelectedIndex;
            if (index < 0
                || this.proxiedConnections[index] is not LiveConnection { IsConnected: true } liveConnection)
            {
                return;
            }

            var packetSender = new PacketSenderForm(liveConnection);
            packetSender.Show(this);
        }

        private void OnBeforeContextMenuOpens(object sender, CancelEventArgs e)
        {
            var index = this.connectedClientsListBox.SelectedIndex;
            var selectedConnection = index < 0 ? null : this.proxiedConnections[index];
            this.disconnectToolStripMenuItem.Enabled = selectedConnection is LiveConnection;
            this.saveToolStripMenuItem.Enabled = selectedConnection != null;
            this.openPacketSenderStripMenuItem.Enabled = selectedConnection is LiveConnection { IsConnected: true };
        }
    }
}