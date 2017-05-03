﻿//******************************************************************************************************
//  Downloader.cs - Gbtc
//
//  Copyright © 2016, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the MIT License (MIT), the "License"; you may not use this
//  file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://opensource.org/licenses/MIT
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  12/08/2015 - J. Ritchie Carroll
//       Generated original version of source code.
//
//******************************************************************************************************

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using DotRas;
using GSF;
using GSF.Configuration;
using GSF.Console;
using GSF.Data;
using GSF.Data.Model;
using GSF.Diagnostics;
using GSF.IO;
using GSF.Net.Ftp;
using GSF.Net.Smtp;
using GSF.Scheduling;
using GSF.Threading;
using GSF.TimeSeries;
using GSF.TimeSeries.Adapters;
using GSF.TimeSeries.Statistics;
using GSF.Units;
using openMIC.Model;

// ReSharper disable UnusedMember.Local
// ReSharper disable UnusedParameter.Local
// ReSharper disable UnusedAutoPropertyAccessor.Local
// ReSharper disable MemberCanBePrivate.Local
namespace openMIC
{
    /// <summary>
    /// Adapter that implements remote file download capabilities.
    /// </summary>
    [Description("Downloader: Implements remote file download capabilities")]
    [EditorBrowsable(EditorBrowsableState.Advanced)] // Normally defined as an input device protocol
    public class Downloader : InputAdapterBase
    {
        #region [ Members ]

        // Nested Types

        // Defines connection profile task settings
        private class ConnectionProfileTaskSettings
        {
            private string m_fileExtensions;
            private string[] m_fileSpecs;

            public ConnectionProfileTaskSettings(string name, int id)
            {
                Name = name;
                ID = id;
            }

            public string Name
            {
                get;
            }

            public int ID
            {
                get;
            }

            [ConnectionStringParameter,
            Description("Defines file names or patterns to download."),
            DefaultValue("*.*")]
            public string FileExtensions
            {
                get
                {
                    return m_fileExtensions;
                }
                set
                {
                    m_fileExtensions = value;
                    m_fileSpecs = null;
                }
            }

            public string[] FileSpecs
            {
                get
                {
                    return m_fileSpecs ?? (m_fileSpecs = (m_fileExtensions ?? "*.*").Split(',').Select(pattern => pattern.Trim()).ToArray());
                }
            }

            [ConnectionStringParameter,
            Description("Defines remote path to download files from ."),
            DefaultValue("/")]
            public string RemotePath
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines local path to download files to."),
            DefaultValue("")]
            public string LocalPath
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if remote folders should scanned for matching downloads - file structure will be replicated locally."),
            DefaultValue(false)]
            public bool RecursiveDownload
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if remote files should be deleted after download."),
            DefaultValue(false)]
            public bool DeleteRemoteFilesAfterDownload
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if total remote files to download should be limited by age."),
            DefaultValue(false)]
            public bool LimitRemoteFileDownloadByAge
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if old local files should be deleted."),
            DefaultValue(false)]
            public bool DeleteOldLocalFiles
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if download should be skipped if local file already exists and matches remote."),
            DefaultValue(false)]
            public bool SkipDownloadIfUnchanged
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if existing local files should be overwritten."),
            DefaultValue(false)]
            public bool OverwriteExistingLocalFiles
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if existing local files should be archived before new ones are downloaded."),
            DefaultValue(false)]
            public bool ArchiveExistingFilesBeforeDownload
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if downloaded file timestamps should be synchronized to remote file timestamps."),
            DefaultValue(true)]
            public bool SynchronizeTimestamps
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines external operation application."),
            DefaultValue("")]
            public string ExternalOperation
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines maximum amount of time, in seconds, to allow the external operation to sit idle."),
            DefaultValue(null)]
            public double? ExternalOperationTimeout
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines maximum file size to download."),
            DefaultValue(1000)]
            public int MaximumFileSize
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines maximum file count to download."),
            DefaultValue(-1)]
            public int MaximumFileCount
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines directory naming expression."),
            DefaultValue("<YYYY><MM>\\<DeviceFolderName>")]
            public string DirectoryNamingExpression
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines directory authentication user name."),
            DefaultValue("")]
            public string DirectoryAuthUserName
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines directory authentication password."),
            DefaultValue("")]
            public string DirectoryAuthPassword
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Determines if an e-mail should be sent if the downloaded files have been updated."),
            DefaultValue(false)]
            public bool EmailOnFileUpdate
            {
                get;
                set;
            }

            [ConnectionStringParameter,
            Description("Defines the recipient e-mail addresses to use when sending e-mails on file updates."),
            DefaultValue("")]
            public string EmailRecipients
            {
                get;
                set;
            }
        }

        // Define a IDevice implementation for to provide daily reports
        private class DeviceProxy : IDevice
        {
            private readonly Downloader m_parent;

            public DeviceProxy(Downloader parent)
            {
                m_parent = parent;
            }

            // Gets or sets total data quality errors of this <see cref="IDevice"/>.
            public long DataQualityErrors
            {
                get;
                set;
            }

            // Gets or sets total time quality errors of this <see cref="IDevice"/>.
            public long TimeQualityErrors
            {
                get;
                set;
            }

            // Gets or sets total device errors of this <see cref="IDevice"/>.
            public long DeviceErrors
            {
                get;
                set;
            }

            // Gets or sets total measurements received for this <see cref="IDevice"/> - in local context "successful connections" per day.
            public long MeasurementsReceived
            {
                get
                {
                    return m_parent.SuccessfulConnections;
                }
                set
                {
                    // Ignoring updates
                }
            }

            // Gets or sets total measurements expected to have been received for this <see cref="IDevice"/> - in local context "attempted connections" per day.
            public long MeasurementsExpected
            {
                get
                {
                    return m_parent.AttemptedConnections;
                }
                set
                {
                    // Ignoring updates
                }
            }

            // Gets or sets the number of measurements received while this <see cref="IDevice"/> was reporting errors.
            public long MeasurementsWithError
            {
                get;
                set;
            }

            // Gets or sets the number of measurements (per frame) defined for this <see cref="IDevice"/>.
            public long MeasurementsDefined
            {
                get;
                set;
            }
        }

        // Define a wrapper to store information about a
        // remote file and the local path it's destined for
        private class FtpFileWrapper
        {
            public readonly string LocalPath;
            public readonly FtpFile RemoteFile;

            public FtpFileWrapper(string localPath, FtpFile remoteFile)
            {
                LocalPath = localPath;
                RemoteFile = remoteFile;
            }

            public void Get()
            {
                RemoteFile.Get(LocalPath);
            }
        }

        // Constants
        private const int NormalPriorty = 1;
        private const int HighPriority = 2;

        // Fields
        private readonly RasDialer m_rasDialer;
        private readonly DeviceProxy m_deviceProxy;
        private readonly object m_connectionProfileLock;
        private Device m_deviceRecord;
        private ConnectionProfile m_connectionProfile;
        private ConnectionProfileTaskSettings[] m_connectionProfileTaskSettings;
        private LogicalThreadOperation m_dialUpOperation;
        private LogicalThreadOperation m_ftpOperation;
        private LongSynchronizedOperation m_executeTasks;
        private readonly ICancellationToken m_cancellationToken;
        private int m_overallTasksCompleted;
        private int m_overallTasksCount;
        private long m_startDialUpTime;
        private bool m_disposed;

        #endregion

        #region [ Constructors ]

        public Downloader()
        {
            m_rasDialer = new RasDialer();
            m_rasDialer.Error += m_rasDialer_Error;
            m_deviceProxy = new DeviceProxy(this);
            m_cancellationToken = new GSF.Threading.CancellationToken();
            m_connectionProfileLock = new object();
        }

        #endregion

        #region [ Properties ]

        /// <summary>
        /// Gets or sets connection host name or IP for transport.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines connection host name or IP for transport.")]
        public string ConnectionHostName
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets connection host user name for transport.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines connection host user name for transport."),
        DefaultValue("anonymous")]
        public string ConnectionUserName
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets connection password for transport.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines connection password for transport."),
        DefaultValue("anonymous")]
        public string ConnectionPassword
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets connection timeout for transport.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines connection timeout for transport."),
        DefaultValue(30000)]
        public int ConnectionTimeout
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets mode of FTP connection.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines mode of FTP connection."),
        DefaultValue(true)]
        public bool PassiveFtp
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets mode of FTP connection.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines IP address to send in FTP PORT command."),
        DefaultValue("")]
        public string ActiveFtpAddress
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets mode of FTP connection.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines minimum port in active FTP port range."),
        DefaultValue(0)]
        public int MinActiveFtpPort
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets mode of FTP connection.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines maximum port in active FTP port range."),
        DefaultValue(0)]
        public int MaxActiveFtpPort
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets flag that determines if connection messages should be logged.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines flag that determines if connection messages should be logged."),
        DefaultValue(false)]
        public bool LogConnectionMessages
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets connection profile record ID.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines connection profile record ID."),
        DefaultValue(0)]
        public int ConnectionProfileID
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets download schedule.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines download schedule."),
        DefaultValue("* * * * *")]
        public string Schedule
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets flag that determines if this connection will use dial-up.
        /// </summary>
        [ConnectionStringParameter,
        Description("Determines if this connection will use dial-up."),
        DefaultValue(false)]
        public bool UseDialUp
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets flag that determines if this connection will use logical threads scheduler.
        /// </summary>
        public bool UseLogicalThread => s_ftpThreadCount > 0;

        /// <summary>
        /// Gets or sets dial-up entry name.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines dial-up entry name."),
        DefaultValue("")]
        public string DialUpEntryName
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets dial-up phone number.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines dial-up phone number."),
        DefaultValue("")]
        public string DialUpNumber
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets dial-up user name.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines dial-up user name."),
        DefaultValue("")]
        public string DialUpUserName
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets dial-up password.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines dial-up password."),
        DefaultValue("")]
        public string DialUpPassword
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets maximum retries for a dial-up connection.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines maximum retries for a dial-up connection."),
        DefaultValue(3)]
        public int DialUpRetries
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets timeout for a dial-up connection.
        /// </summary>
        [ConnectionStringParameter,
        Description("Defines timeout for a dial-up connection."),
        DefaultValue(90)]
        public int DialUpTimeout
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of attempted connections.
        /// </summary>
        public long AttemptedConnections
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of successful connections.
        /// </summary>
        public long SuccessfulConnections
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of failed connections.
        /// </summary>
        public long FailedConnections
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of processed files.
        /// </summary>
        public long TotalProcessedFiles
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of attempted dial-ups.
        /// </summary>
        public long AttemptedDialUps
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of successful dial-ups.
        /// </summary>
        public long SuccessfulDialUps
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of failed dial-ups.
        /// </summary>
        public long FailedDialUps
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets number of files downloaded during last execution.
        /// </summary>
        public long FilesDownloaded
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of files downloaded.
        /// </summary>
        public long TotalFilesDownloaded
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total number of bytes downloaded.
        /// </summary>
        public long BytesDownloaded
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total connected time, in ticks.
        /// </summary>
        public long TotalConnectedTime
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets total dial-up time, in ticks.
        /// </summary>
        public long TotalDialUpTime
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets <see cref="DataSet" /> based data source available to this <see cref="AdapterBase" />.
        /// </summary>
        public override DataSet DataSource
        {
            get
            {
                return base.DataSource;
            }

            set
            {
                base.DataSource = value;

                // ReloadConfig was requested, take this opportunity to reload connection profile tasks...
                ThreadPool.QueueUserWorkItem(state =>
                {
                    try
                    {
                        LoadTasks();
                    }
                    catch (Exception ex)
                    {
                        OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to reload connection profile tasks: {ex.Message}", ex));
                    }
                });
            }
        }

        /// <summary>
        /// Gets flag that determines if the data input connects asynchronously.
        /// </summary>
        /// <remarks>
        /// Derived classes should return true when data input source is connects asynchronously, otherwise return false.
        /// </remarks>
        protected override bool UseAsyncConnect => false;

        /// <summary>
        /// Gets the flag indicating if this adapter supports temporal processing.
        /// </summary>
        public override bool SupportsTemporalProcessing => false;

        /// <summary>
        /// Returns the detailed status of the data input source.
        /// </summary>
        public override string Status
        {
            get
            {
                StringBuilder status = new StringBuilder();

                status.Append(base.Status);
                status.AppendFormat("      Connection host name: {0}", ConnectionHostName.ToNonNullNorWhiteSpace("undefined"));
                status.AppendLine();
                status.AppendFormat("      Connection user name: {0} - with {1} password", ConnectionUserName.ToNonNullNorWhiteSpace("undefined"), string.IsNullOrWhiteSpace(ConnectionPassword) ? "no" : "a");
                status.AppendLine();
                status.AppendFormat("     Connection profile ID: {0} - {1}", ConnectionProfileID, m_connectionProfile?.Name ?? "undefined");
                status.AppendLine();
                status.AppendFormat("         Download schedule: {0}", Schedule);
                status.AppendLine();
                status.AppendFormat("   Log connection messages: {0}", LogConnectionMessages);
                status.AppendLine();
                status.AppendFormat("     Attempted connections: {0}", AttemptedConnections);
                status.AppendLine();
                status.AppendFormat("    Successful connections: {0}", SuccessfulConnections);
                status.AppendLine();
                status.AppendFormat("        Failed connections: {0}", FailedConnections);
                status.AppendLine();
                status.AppendFormat("     Total processed files: {0}", TotalProcessedFiles);
                status.AppendLine();
                status.AppendFormat("      Total connected time: {0}", new Ticks(TotalConnectedTime).ToElapsedTimeString(3));
                status.AppendLine();
                status.AppendFormat("               Use dial-up: {0}", UseDialUp);
                status.AppendLine();

                if (UseDialUp)
                {
                    status.AppendFormat("        Dial-up entry name: {0}", DialUpEntryName);
                    status.AppendLine();
                    status.AppendFormat("            Dial-up number: {0}", DialUpNumber);
                    status.AppendLine();
                    status.AppendFormat("         Dial-up user name: {0} - with {1} password", DialUpUserName.ToNonNullNorWhiteSpace("undefined"), string.IsNullOrWhiteSpace(DialUpPassword) ? "no" : "a");
                    status.AppendLine();
                    status.AppendFormat("           Dial-up retries: {0}", DialUpRetries);
                    status.AppendLine();
                    status.AppendFormat("          Dial-up time-out: {0}", DialUpTimeout);
                    status.AppendLine();
                    status.AppendFormat("        Attempted dial-ups: {0}", AttemptedDialUps);
                    status.AppendLine();
                    status.AppendFormat("       Successful dial-ups: {0}", SuccessfulDialUps);
                    status.AppendLine();
                    status.AppendFormat("           Failed dial-ups: {0}", FailedDialUps);
                    status.AppendLine();
                    status.AppendFormat("        Total dial-up time: {0}", new Ticks(TotalDialUpTime).ToElapsedTimeString(3));
                    status.AppendLine();
                }

                status.AppendFormat(" Connection profiles tasks: {0}", m_connectionProfileTaskSettings.Length);
                status.AppendLine();
                status.AppendFormat("          Files downloaded: {0}", FilesDownloaded);
                status.AppendLine();
                status.AppendFormat("          Bytes downloaded: {0:N3} MB", BytesDownloaded / (double)SI2.Mega);
                status.AppendLine();

                return status.ToString();
            }
        }


        // Gets RAS connection state
        private RasConnectionState RasState => RasConnection.GetActiveConnections().FirstOrDefault(ras => ras.EntryName == DialUpEntryName)?.GetConnectionStatus()?.ConnectionState ?? RasConnectionState.Disconnected;

        #endregion

        #region [ Methods ]

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="Downloader"/> object and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (!m_disposed)
            {
                try
                {
                    if (disposing)
                    {
                        m_cancellationToken.Cancel();
                        DeregisterSchedule(this);

                        if ((object)m_rasDialer != null)
                        {
                            m_rasDialer.Error -= m_rasDialer_Error;
                            m_rasDialer.Dispose();
                        }

                        StatisticsEngine.Unregister(m_deviceProxy);
                        StatisticsEngine.Unregister(this);
                    }
                }
                finally
                {
                    m_disposed = true;          // Prevent duplicate dispose.
                    base.Dispose(disposing);    // Call base class Dispose().
                }
            }
        }

        /// <summary>
        /// Initializes <see cref="Downloader"/>.
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();
            ConnectionStringParser<ConnectionStringParameterAttribute> parser = new ConnectionStringParser<ConnectionStringParameterAttribute>();

            parser.ParseConnectionString(ConnectionString, this);

            LoadTasks();
            RegisterSchedule(this);

            // Register downloader with the statistics engine
            StatisticsEngine.Register(this, "Downloader", "DLR");
            StatisticsEngine.Register(m_deviceProxy, Name, "Device", "PMU");
        }

        /// <summary>
        /// Attempts to connect to data input source.
        /// </summary>
        protected override void AttemptConnection()
        {
            ConnectionProfileTaskSettings[] taskSettings;

            lock (m_connectionProfileLock)
                taskSettings = m_connectionProfileTaskSettings;

            foreach (ConnectionProfileTaskSettings settings in taskSettings)
            {
                string localPath = settings.LocalPath.ToNonNullString().Trim();

                if (localPath.StartsWith(@"\\") && !string.IsNullOrWhiteSpace(settings.DirectoryAuthUserName) && !string.IsNullOrWhiteSpace(settings.DirectoryAuthPassword))
                {
                    string[] userParts = settings.DirectoryAuthUserName.Split('\\');

                    try
                    {
                        if (userParts.Length == 2)
                            FilePath.ConnectToNetworkShare(localPath.Trim(), userParts[1].Trim(), settings.DirectoryAuthPassword.Trim(), userParts[0].Trim());
                        else
                            throw new InvalidOperationException($"UNC based local path \"{settings.LocalPath}\" or authentication user name \"{settings.DirectoryAuthUserName}\" is not in the correct format.");
                    }
                    catch (Exception ex)
                    {
                        OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Exception while authenticating UNC path \"{settings.LocalPath}\": {ex.Message}", ex));
                    }
                }
            }
        }

        /// <summary>
        /// Attempts to disconnect from data input source.
        /// </summary>
        protected override void AttemptDisconnection()
        {
            // Just leaving UNC paths authenticated for the duration of service run-time since multiple
            // devices may share the same root destination path
        }

        /// <summary>
        /// Gets a short one-line status of this adapter.
        /// </summary>
        /// <param name="maxLength">Maximum number of available characters for display.</param>
        /// <returns>
        /// A short one-line summary of the current status of this adapter.
        /// </returns>
        public override string GetShortStatus(int maxLength)
        {
            if (!Enabled)
                return "Downloading for is paused...".CenterText(maxLength);

            return $"Downloading enabled for schedule: {Schedule}".CenterText(maxLength);
        }

        /// <summary>
        /// Queues scheduled tasks for immediate execution.
        /// </summary>
        [AdapterCommand("Queues scheduled tasks for immediate execution.", "Administrator", "Editor")]
        public void QueueTasks()
        {
            ThreadPool.QueueUserWorkItem(state =>
            {
                AttemptedConnections++;

                if (UseDialUp)
                {
                    m_dialUpOperation.Priority = HighPriority;
                    m_dialUpOperation.RunOnce();
                    m_dialUpOperation.Priority = NormalPriorty;
                }
                else if (UseLogicalThread)
                {
                    m_ftpOperation.RunOnce();
                }
                else { 
                    m_executeTasks.RunOnce();
                }
            });
        }

        private void LoadTasks()
        {
            ConnectionStringParser<ConnectionStringParameterAttribute> parser = new ConnectionStringParser<ConnectionStringParameterAttribute>();

            using (AdoDataConnection connection = new AdoDataConnection("systemSettings"))
            {
                TableOperations<Device> deviceTable = new TableOperations<Device>(connection);
                TableOperations<ConnectionProfile> connectionProfileTable = new TableOperations<ConnectionProfile>(connection);
                TableOperations<ConnectionProfileTask> connectionProfileTaskTable = new TableOperations<ConnectionProfileTask>(connection);

                lock (m_connectionProfileLock)
                {
                    m_deviceRecord = deviceTable.QueryRecordWhere("Acronym = {0}", Name);
                    m_connectionProfile = connectionProfileTable.LoadRecord(ConnectionProfileID);
                    IEnumerable<ConnectionProfileTask> tasks = connectionProfileTaskTable.QueryRecords(restriction: new RecordRestriction("ConnectionProfileID={0}", ConnectionProfileID));
                    List<ConnectionProfileTaskSettings> connectionProfileTaskSettings = new List<ConnectionProfileTaskSettings>();

                    foreach (ConnectionProfileTask task in tasks)
                    {
                        ConnectionProfileTaskSettings settings = new ConnectionProfileTaskSettings(task.Name, task.ID);
                        parser.ParseConnectionString(task.Settings, settings);
                        connectionProfileTaskSettings.Add(settings);
                    }

                    m_connectionProfileTaskSettings = connectionProfileTaskSettings.ToArray();
                }
            }
        }

        private void ExecuteTasks()
        {
            if (m_cancellationToken.IsCancelled)
                return;

            FtpClient client = null;
            Ticks connectionStartTime = DateTime.UtcNow.Ticks;
            string connectionProfileName = m_connectionProfile?.Name ?? "Undefined";

            try
            {
                ConnectionProfileTaskSettings[] taskSettings;
                List<ConnectionProfileTaskSettings> ftpTaskSettings;
                List<ConnectionProfileTaskSettings> externalOperationTaskSettings;

                lock (m_connectionProfileLock)
                    taskSettings = m_connectionProfileTaskSettings;

                if (taskSettings.Length == 0)
                {
                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Skipped, true, $"Skipped \"{connectionProfileName}\" connection profile processing: No tasks defined.", 0, 1));
                    return;
                }

                ftpTaskSettings = taskSettings.Where(settings => string.IsNullOrWhiteSpace(settings.ExternalOperation)).ToList();
                externalOperationTaskSettings = taskSettings.Where(settings => !string.IsNullOrWhiteSpace(settings.ExternalOperation)).ToList();

                FilesDownloaded = 0;
                m_overallTasksCompleted = 0;
                m_overallTasksCount = taskSettings.Length;
                OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, $"Starting \"{connectionProfileName}\" connection profile processing...", m_overallTasksCompleted, m_overallTasksCount));

                if (ftpTaskSettings.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(ConnectionHostName))
                    {
                        OnStatusMessage(MessageLevel.Warning, "No connection host name provided, skipping connection to FTP server...");
                    }
                    else
                    {
                        OnStatusMessage(MessageLevel.Info, $"Attempting connection to FTP server \"{ConnectionUserName}@{ConnectionHostName}\"...");

                        client = new FtpClient();
                        client.CommandSent += FtpClient_CommandSent;
                        client.ResponseReceived += FtpClient_ResponseReceived;
                        client.FileTransferProgress += FtpClient_FileTransferProgress;
                        client.FileTransferNotification += FtpClient_FileTransferNotification;

                        string[] parts = ConnectionHostName.Split(':');

                        if (parts.Length > 1)
                        {
                            client.Server = parts[0];
                            client.Port = int.Parse(parts[1]);
                        }
                        else
                        {
                            client.Server = ConnectionHostName;
                        }

                        client.Timeout = ConnectionTimeout;
                        client.Passive = PassiveFtp;
                        client.ActiveAddress = ActiveFtpAddress;
                        client.MinActivePort = MinActiveFtpPort;
                        client.MaxActivePort = MaxActiveFtpPort;
                        client.Connect(ConnectionUserName, ConnectionPassword);

                        SuccessfulConnections++;
                        OnStatusMessage(MessageLevel.Info, $"Connected to FTP server \"{ConnectionUserName}@{ConnectionHostName}\"");
                    }
                }

                foreach (ConnectionProfileTaskSettings settings in ftpTaskSettings)
                {
                    OnStatusMessage(MessageLevel.Info, $"Starting \"{connectionProfileName}\" connection profile \"{settings.Name}\" task processing:");

                    ProcessFTPTask(settings, client);

                    // Handle local file age limit processing, if enabled
                    if (settings.DeleteOldLocalFiles)
                        HandleLocalFileAgeLimitProcessing(settings);

                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, null, ++m_overallTasksCompleted, m_overallTasksCount));
                }

                foreach (ConnectionProfileTaskSettings settings in externalOperationTaskSettings)
                {
                    OnStatusMessage(MessageLevel.Info, $"Starting \"{connectionProfileName}\" connection profile \"{settings.Name}\" task processing:");

                    ProcessExternalOperationTask(settings);

                    // Handle local file age limit processing, if enabled
                    if (settings.DeleteOldLocalFiles)
                        HandleLocalFileAgeLimitProcessing(settings);

                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, null, ++m_overallTasksCompleted, m_overallTasksCount));
                }

                OnProgressUpdated(this, new ProgressUpdate(ProgressState.Succeeded, true, $"Completed \"{connectionProfileName}\" connection profile processing.", m_overallTasksCount, m_overallTasksCount));
            }
            catch (Exception ex)
            {
                FailedConnections++;

                if ((object)client != null)
                {
                    OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to connect to FTP server \"{ConnectionUserName}@{ConnectionHostName}\": {ex.Message}", ex));
                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, $"Failed to connect to FTP server \"{ConnectionUserName}@{ConnectionHostName}\": {ex.Message}", m_overallTasksCompleted, m_overallTasksCount));
                }
                else
                {
                    OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to process connection profile tasks for \"{connectionProfileName}\": {ex.Message}", ex));
                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, $"Failed to process connection profile tasks for \"{connectionProfileName}\": {ex.Message}", m_overallTasksCompleted, m_overallTasksCount));
                }

                TryUpdateStatusLogTable(null, "", false, ex.Message);
            }
            finally
            {
                if ((object)client != null)
                {
                    client.CommandSent -= FtpClient_CommandSent;
                    client.ResponseReceived -= FtpClient_ResponseReceived;
                    client.FileTransferProgress -= FtpClient_FileTransferProgress;
                    client.FileTransferNotification -= FtpClient_FileTransferNotification;
                    client.Dispose();

                    Ticks connectedTime = DateTime.UtcNow.Ticks - connectionStartTime;
                    OnStatusMessage(MessageLevel.Info, $"FTP session connected for {connectedTime.ToElapsedTimeString(2)}");
                    TotalConnectedTime += connectedTime;
                }

                OnProgressUpdated(this, new ProgressUpdate(ProgressState.Finished, true, "", 1, 1));
            }
        }

        private void ProcessFTPTask(ConnectionProfileTaskSettings settings, FtpClient client)
        {
            string remotePath = GetRemotePathDirectory(settings);
            string localDirectoryPath = GetLocalPathDirectory(settings);
            List<FtpFileWrapper> files = new List<FtpFileWrapper>();

            OnStatusMessage(MessageLevel.Info, $"Ensuring local path \"{localDirectoryPath}\" exists.");
            Directory.CreateDirectory(localDirectoryPath);

            OnStatusMessage(MessageLevel.Info, $"Building list of files to be downloaded from \"{remotePath}\".");
            BuildFileList(files, settings, client, remotePath, localDirectoryPath);
            DownloadAllFiles(files, client, settings);
        }

        private void BuildFileList(List<FtpFileWrapper> fileList, ConnectionProfileTaskSettings settings, FtpClient client, string remotePath, string localDirectoryPath)
        {
            if (m_cancellationToken.IsCancelled)
                return;

            OnStatusMessage(MessageLevel.Info, $"Attempting to set remote FTP directory path \"{remotePath}\"...");
            client.SetCurrentDirectory(remotePath);

            OnStatusMessage(MessageLevel.Info, $"Enumerating remote files in \"{remotePath}\"...");

            foreach (FtpFile file in client.CurrentDirectory.Files)
            {
                if (m_cancellationToken.IsCancelled)
                    return;

                if (!FilePath.IsFilePatternMatch(settings.FileSpecs, file.Name, true))
                    continue;

                if (settings.LimitRemoteFileDownloadByAge && (DateTime.Now - file.Timestamp).Days > Program.Host.Model.Global.MaxRemoteFileAge)
                {
                    OnStatusMessage(MessageLevel.Info, $"File \"{file.Name}\" skipped, timestamp \"{file.Timestamp:yyyy-MM-dd HH:mm.ss.fff}\" is older than {Program.Host.Model.Global.MaxRemoteFileAge} days.");
                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, $"File \"{file.Name}\" skipped: File is too old.", m_overallTasksCompleted, m_overallTasksCount));
                    continue;
                }

                if (file.Size > settings.MaximumFileSize * SI2.Mega)
                {
                    OnStatusMessage(MessageLevel.Info, $"File \"{file.Name}\" skipped, size of {file.Size / SI2.Mega:N3} MB is larger than {settings.MaximumFileSize:N3} MB configured limit.");
                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, $"File \"{file.Name}\" skipped: File is too large ({file.Size / (double)SI2.Mega:N3} MB).", m_overallTasksCompleted, m_overallTasksCount));
                    continue;
                }

                string localPath = Path.Combine(localDirectoryPath, file.Name);

                if (File.Exists(localPath) && settings.SkipDownloadIfUnchanged)
                {
                    try
                    {
                        FileInfo info = new FileInfo(localPath);

                        // Compare file sizes and timestamps
                        bool localEqualsRemote =
                            info.Length == file.Size &&
                            (!settings.SynchronizeTimestamps || info.LastWriteTime == file.Timestamp);

                        if (localEqualsRemote)
                        {
                            OnStatusMessage(MessageLevel.Info, $"Skipping file download for remote file \"{file.Name}\": Local file already exists and matches remote file.");
                            OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, $"File \"{file.Name}\" skipped: Local file already exists and matches remote file", m_overallTasksCompleted, m_overallTasksCount));
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Unable to determine whether local file size and time matches remote file size and time due to exception: {ex.Message}", ex));
                    }
                }

                fileList.Add(new FtpFileWrapper(localPath, file));
            }

            if (settings.RecursiveDownload)
            {
                FtpDirectory[] directories = new FtpDirectory[0];

                try
                {
                    OnStatusMessage(MessageLevel.Info, $"Enumerating remote directories in \"{remotePath}\"...");
                    directories = client.CurrentDirectory.SubDirectories.ToArray();
                }
                catch (Exception ex)
                {
                    OnProcessException(MessageLevel.Error, new Exception($"Failed to enumerate remote directories in \"{remotePath}\" due to exception: {ex.Message}", ex));
                }

                foreach (FtpDirectory directory in directories)
                {
                    try
                    {
                        if (m_cancellationToken.IsCancelled)
                            return;

                        string directoryName = directory.Name;

                        if (directoryName.StartsWith(".", StringComparison.Ordinal))
                            continue;

                        string remoteSubPath = $"{remotePath}/{directoryName}";
                        string localSubPath = Path.Combine(localDirectoryPath, directoryName);

                        OnStatusMessage(MessageLevel.Info, $"Recursively adding files in \"{remotePath}\" to file list...");
                        BuildFileList(fileList, settings, client, remoteSubPath, localSubPath);
                    }
                    catch (Exception ex)
                    {
                        OnProcessException(MessageLevel.Error, new Exception($"Failed to add remote files from remote directory \"{directory.Name}\" to file list due to exception: {ex.Message}", ex));
                    }
                }
            }
        }

        private void DownloadAllFiles(List<FtpFileWrapper> files, FtpClient client, ConnectionProfileTaskSettings settings)
        {
            long progress = 0L;
            long totalBytes = files.Sum(wrapper => wrapper.RemoteFile.Size);

            if (m_cancellationToken.IsCancelled)
                return;

            // Group files by destination directory so we can skip whole groups
            // of files if the directory does not exist and cannot be created
            foreach (IGrouping<string, FtpFileWrapper> grouping in files.GroupBy(wrapper => Path.GetDirectoryName(wrapper.LocalPath)))
            {
                if (m_cancellationToken.IsCancelled)
                    return;

                try
                {
                    Directory.CreateDirectory(grouping.Key);
                }
                catch (Exception ex)
                {
                    string message = $"Failed to create local directory for {grouping.Count()} remote files due to exception: {ex.Message}";
                    OnProcessException(MessageLevel.Error, new Exception(message, ex));
                    progress += grouping.Sum(wrapper => wrapper.RemoteFile.Size);
                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, message, m_overallTasksCompleted * totalBytes + progress, totalBytes * m_overallTasksCount));
                    continue;
                }

                foreach (FtpFileWrapper wrapper in grouping)
                {
                    if (m_cancellationToken.IsCancelled)
                        return;

                    OnStatusMessage(MessageLevel.Info, $"Verifying logic allows for download of remote file \"{wrapper.RemoteFile.Name}\"...");

                    // Update progress in advance in case the transfer fails
                    progress += wrapper.RemoteFile.Size;
                    TotalProcessedFiles++;

                    bool fileUpdated = File.Exists(wrapper.LocalPath) && settings.SkipDownloadIfUnchanged;

                    if (File.Exists(wrapper.LocalPath) && settings.ArchiveExistingFilesBeforeDownload)
                    {
                        try
                        {
                            string directoryName = Path.Combine(grouping.Key, "Archive\\");
                            string archiveFileName = Path.Combine(directoryName, wrapper.RemoteFile.Name);

                            Directory.CreateDirectory(directoryName);

                            if (File.Exists(archiveFileName))
                                archiveFileName = FilePath.GetUniqueFilePathWithBinarySearch(archiveFileName);

                            OnStatusMessage(MessageLevel.Info, $"Archiving existing file \"{wrapper.LocalPath}\" to \"{archiveFileName}\"...");
                            File.Move(wrapper.LocalPath, archiveFileName);
                        }
                        catch (Exception ex)
                        {
                            OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to archive existing local file \"{wrapper.LocalPath}\" due to exception: {ex.Message}", ex));
                        }
                    }

                    if (File.Exists(wrapper.LocalPath) && !settings.OverwriteExistingLocalFiles)
                    {
                        OnStatusMessage(MessageLevel.Info, $"Skipping file download for remote file \"{wrapper.RemoteFile.Name}\": Local file already exists and settings do not allow overwrite.");
                        OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, $"File \"{wrapper.RemoteFile.Name}\" skipped: Local file already exists", m_overallTasksCompleted * totalBytes + progress, totalBytes * m_overallTasksCount));
                        continue;
                    }

                    try
                    {
                        // Download the file
                        OnStatusMessage(MessageLevel.Info, $"Downloading remote file from \"{wrapper.RemoteFile.FullPath}\" to local path \"{wrapper.LocalPath}\"...");
                        wrapper.Get();

                        if (settings.DeleteRemoteFilesAfterDownload)
                        {
                            try
                            {
                                // Remove the remote file
                                OnStatusMessage(MessageLevel.Info, $"Removing file \"{wrapper.RemoteFile.FullPath}\" from remote server...");
                                wrapper.RemoteFile.Remove();
                            }
                            catch (Exception ex)
                            {
                                string message = $"Failed to remove file \"{wrapper.RemoteFile.FullPath}\" from remote server due to exception: {ex.Message}";
                                OnProcessException(MessageLevel.Warning, new InvalidOperationException(message, ex));
                                OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, message, m_overallTasksCompleted * totalBytes + progress, totalBytes * m_overallTasksCount));
                            }
                        }

                        // Update these statistics only if
                        // the file download was successful
                        FilesDownloaded++;
                        TotalFilesDownloaded++;
                        BytesDownloaded += wrapper.RemoteFile.Size;
                        OnProgressUpdated(this, new ProgressUpdate(ProgressState.Succeeded, true, $"Successfully downloaded remote file \"{wrapper.RemoteFile.FullPath}\".", m_overallTasksCompleted * totalBytes + progress, totalBytes * m_overallTasksCount));

                        // Synchronize local timestamp to that of remote file if requested
                        if (settings.SynchronizeTimestamps)
                        {
                            FileInfo info = new FileInfo(wrapper.LocalPath);
                            info.LastAccessTime = info.LastWriteTime = wrapper.RemoteFile.Timestamp;
                        }

                        TryUpdateStatusLogTable(wrapper.RemoteFile, wrapper.LocalPath, true);
                    }
                    catch (Exception ex)
                    {
                        string message = $"Failed to download remote file \"{wrapper.RemoteFile.FullPath}\" due to exception: {ex.Message}";
                        OnProcessException(MessageLevel.Warning, new InvalidOperationException(message, ex));
                        OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, message, m_overallTasksCompleted * totalBytes + progress, totalBytes * m_overallTasksCount));
                        TryUpdateStatusLogTable(wrapper.RemoteFile, wrapper.LocalPath, false);
                    }

                    // Send e-mail on file update, if requested
                    if (fileUpdated && settings.EmailOnFileUpdate)
                    {
                        ThreadPool.QueueUserWorkItem(state =>
                        {
                            try
                            {
                                GlobalSettings global = Program.Host.Model.Global;
                                string subject = $"File changed for \"{Name}: {settings.Name}\"";
                                string body = $"<b>File Name = {wrapper.LocalPath}</b></br>";

                                if (string.IsNullOrWhiteSpace(global.SmtpUserName))
                                    Mail.Send(global.FromAddress, settings.EmailRecipients, subject, body, true, global.SmtpServer);
                                else
                                    Mail.Send(global.FromAddress, settings.EmailRecipients, subject, body, true, global.SmtpServer, global.SmtpUserName, global.SmtpPassword);
                            }
                            catch (Exception ex)
                            {
                                OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to send e-mail notification about updated file \"{wrapper.LocalPath}\": {ex.Message}"));
                            }
                        });
                    }
                }
            }
        }

        private string GetLocalPathDirectory(ConnectionProfileTaskSettings settings)
        {
            Dictionary<string, string> substitutions = new Dictionary<string, string>
            {
                { "<YYYY>", $"{DateTime.Now.Year}" },
                { "<YY>", $"{DateTime.Now.Year.ToString().Substring(2)}" },
                { "<MM>", $"{DateTime.Now.Month.ToString().PadLeft(2, '0')}" },
                { "<DD>", $"{DateTime.Now.Day.ToString().PadLeft(2, '0')}" },
                { "<DeviceName>", m_deviceRecord.Name ?? "undefined" },
                { "<DeviceAcronym>", m_deviceRecord.Acronym },
                { "<DeviceFolderName>", m_deviceRecord.OriginalSource ?? m_deviceRecord.Acronym },
                { "<ProfileName>", m_connectionProfile.Name ?? "undefined" }
            };

            string subPath = substitutions.Aggregate(settings.DirectoryNamingExpression, (expression, kvp) => expression.Replace(kvp.Key, kvp.Value));

            if (!string.IsNullOrEmpty(settings.LocalPath))
                subPath = subPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            
            string directoryName = Path.Combine(settings.LocalPath, subPath);

            if (!Directory.Exists(directoryName))
            {
                try
                {
                    Directory.CreateDirectory(directoryName);
                }
                catch (Exception ex)
                {
                    OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to create directory \"{directoryName}\": {ex.Message}", ex));
                }
            }

            return directoryName;
        }

        private string GetRemotePathDirectory(ConnectionProfileTaskSettings settings)
        {
            Dictionary<string, string> substitutions = new Dictionary<string, string>
            {
                { "<YYYY>", $"{DateTime.Now.Year}" },
                { "<YY>", $"{DateTime.Now.Year.ToString().Substring(2)}" },
                { "<MM>", $"{DateTime.Now.Month.ToString().PadLeft(2, '0')}" },
                { "<DD>", $"{DateTime.Now.Day.ToString().PadLeft(2, '0')}" },
                { "<Month MM>", $"Month {DateTime.Now.Month.ToString().PadLeft(2, '0')}" },
                { "<Day DD>", $"Day {DateTime.Now.Day.ToString().PadLeft(2, '0')}" },
                { "<Day DD-1>", $"Day {DateTime.Now.AddDays(-1).Day.ToString().PadLeft(2, '0')}" },
                { "<DeviceName>", m_deviceRecord.Name ?? "undefined" },
                { "<DeviceAcronym>", m_deviceRecord.Acronym },
                { "<DeviceFolderName>", m_deviceRecord.OriginalSource ?? m_deviceRecord.Acronym },
                { "<ProfileName>", m_connectionProfile.Name ?? "undefined" }
            };

            if(settings.RemotePath.Contains("<Day DD-1>"))
            {
                substitutions["<YYYY>"] = $"{DateTime.Now.AddDays(-1).Year}";
                substitutions["<YY>"] = $"{DateTime.Now.AddDays(-1).Year.ToString().Substring(2)}";
                substitutions["<MM>"] = $"{DateTime.Now.AddDays(-1).Month.ToString().PadLeft(2, '0')}";
                substitutions["<Month MM>"] = $"Month {DateTime.Now.AddDays(-1).Month.ToString().PadLeft(2, '0')}";
            }

            return substitutions.Aggregate(settings.RemotePath, (path, sub) => path.Replace(sub.Key, sub.Value));
        }

        private void ProcessExternalOperationTask(ConnectionProfileTaskSettings settings)
        {
            string localPathDirectory = GetLocalPathDirectory(settings);

            Dictionary<string, string> substitutions = new Dictionary<string, string>
            {
                { "<DeviceID>", m_deviceRecord.ID.ToString() },
                { "<DeviceName>", m_deviceRecord.Name ?? "undefined" },
                { "<DeviceAcronym>", m_deviceRecord.Acronym },
                { "<DeviceFolderName>", m_deviceRecord.OriginalSource ?? m_deviceRecord.Acronym },
                { "<DeviceFolderPath>", GetLocalPathDirectory(settings) },
                { "<ProfileName>", m_connectionProfile.Name ?? "undefined" },
                { "<TaskID>", settings.ID.ToString() }
            };

            string command = substitutions.Aggregate(settings.ExternalOperation.Trim(), (str, kvp) => str.Replace(kvp.Key, kvp.Value));
            string executable = Arguments.ParseCommand(command)[0];
            string args = command.Substring(executable.Length).Trim();
            TimeSpan timeout = TimeSpan.FromSeconds(settings.ExternalOperationTimeout ?? ConnectionTimeout / 1000.0D);

            OnStatusMessage(MessageLevel.Info, $"Executing external operation \"{command}\"...");
            OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, "Starting external action...", m_overallTasksCompleted, m_overallTasksCount));

            try
            {
                int fileCount = FilePath.EnumerateFiles(localPathDirectory, "*", SearchOption.AllDirectories).Count();

                using (SafeFileWatcher fileWatcher = new SafeFileWatcher(localPathDirectory))
                using (Process externalOperation = new Process())
                {
                    DateTime lastUpdate = DateTime.UtcNow;

                    fileWatcher.Created += (sender, fileArgs) => lastUpdate = DateTime.UtcNow;
                    fileWatcher.Changed += (sender, fileArgs) => lastUpdate = DateTime.UtcNow;
                    fileWatcher.Deleted += (sender, fileArgs) => lastUpdate = DateTime.UtcNow;
                    fileWatcher.EnableRaisingEvents = true;

                    externalOperation.StartInfo.FileName = executable;
                    externalOperation.StartInfo.Arguments = args;
                    externalOperation.StartInfo.RedirectStandardOutput = true;
                    externalOperation.StartInfo.RedirectStandardError = true;
                    externalOperation.StartInfo.UseShellExecute = false;
                    externalOperation.StartInfo.WorkingDirectory = Directory.GetCurrentDirectory();

                    externalOperation.OutputDataReceived += (sender, processArgs) =>
                    {
                        if (string.IsNullOrWhiteSpace(processArgs.Data))
                            return;

                        lastUpdate = DateTime.UtcNow;
                        OnStatusMessage(MessageLevel.Info, processArgs.Data);
                        OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, true, processArgs.Data, m_overallTasksCompleted, m_overallTasksCount));
                    };

                    externalOperation.ErrorDataReceived += (sender, processArgs) =>
                    {
                        if (string.IsNullOrWhiteSpace(processArgs.Data))
                            return;

                        lastUpdate = DateTime.UtcNow;
                        OnProcessException(MessageLevel.Error, new Exception(processArgs.Data));
                        OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, processArgs.Data, m_overallTasksCompleted, m_overallTasksCount));
                    };

                    externalOperation.Start();
                    externalOperation.BeginOutputReadLine();
                    externalOperation.BeginErrorReadLine();

                    while (!externalOperation.WaitForExit(1000))
                    {
                        if (m_cancellationToken.IsCancelled)
                        {
                            TerminateProcessTree(externalOperation.Id);
                            OnProcessException(MessageLevel.Warning, new InvalidOperationException($"External operation \"{command}\" forcefully terminated: downloader was disabled."));
                            OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, "External operation forcefully terminated: downloader was disabled.", m_overallTasksCompleted, m_overallTasksCount));
                            return;
                        }

                        if (DateTime.UtcNow - lastUpdate > timeout)
                        {
                            TerminateProcessTree(externalOperation.Id);
                            OnProcessException(MessageLevel.Error, new InvalidOperationException($"External operation \"{command}\" forcefully terminated: exceeded timeout ({timeout.TotalSeconds:0.##} seconds)."));
                            OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, $"External operation forcefully terminated: exceeded timeout ({timeout.TotalSeconds:0.##} seconds).", m_overallTasksCompleted, m_overallTasksCount));
                            return;
                        }
                    }

                    int filesDownloaded = FilePath.EnumerateFiles(localPathDirectory, "*", SearchOption.AllDirectories).Count() - fileCount;
                    FilesDownloaded += filesDownloaded;
                    TotalFilesDownloaded += filesDownloaded;
                    OnStatusMessage(MessageLevel.Info, $"External operation \"{command}\" completed with status code {externalOperation.ExitCode}.");
                    OnProgressUpdated(this, new ProgressUpdate(ProgressState.Succeeded, true, $"External action complete: exit code {externalOperation.ExitCode}.", m_overallTasksCompleted, m_overallTasksCount));
                }
            }
            catch (Exception ex)
            {
                OnProcessException(MessageLevel.Error, new InvalidOperationException($"Failed to execute external operation \"{command}\": {ex.Message}", ex));
                OnProgressUpdated(this, new ProgressUpdate(ProgressState.Failed, true, $"Failed to execute external action: {ex.Message}", m_overallTasksCompleted, m_overallTasksCount));
            }
        }

        private void HandleLocalFileAgeLimitProcessing(ConnectionProfileTaskSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.LocalPath) || !Directory.Exists(settings.LocalPath))
            {
                OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Cannot handle local file age limit processing for connection profile task \"{settings.Name}\": Local path \"{settings.LocalPath ?? ""}\" does not exist."));
                return;
            }

            OnStatusMessage(MessageLevel.Info, $"Enumerating local files in \"{settings.LocalPath}\"...");

            try
            {
                string[] files = FilePath.GetFileList(Path.Combine(FilePath.GetAbsolutePath(settings.LocalPath), "*\\*.*"));
                long deletedCount = 0;

                OnStatusMessage(MessageLevel.Info, $"Found {files.Length} local files, starting age limit processing...");

                foreach (string file in files)
                {
                    // Check file specification restriction
                    if (!FilePath.IsFilePatternMatch(settings.FileSpecs, file, true))
                        continue;

                    DateTime creationTime = File.GetCreationTime(file);

                    if ((DateTime.Now - creationTime).Days > Program.Host.Model.Global.MaxLocalFileAge)
                    {
                        OnStatusMessage(MessageLevel.Info, $"Attempting to delete file \"{file}\" created at \"{creationTime:yyyy-MM-dd HH:mm.ss.fff}\"...");

                        try
                        {
                            string rootPathName = FilePath.GetDirectoryName(settings.LocalPath);
                            string directoryName = FilePath.GetDirectoryName(file);

                            FilePath.WaitForWriteLock(file);
                            File.Delete(file);
                            deletedCount++;
                            OnStatusMessage(MessageLevel.Info, $"File \"{file}\" successfully deleted...");

                            if (!directoryName.Equals(rootPathName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Try to remove sub-folder, this will only succeed if folder is empty...
                                try
                                {
                                    Directory.Delete(directoryName);
                                    OnStatusMessage(MessageLevel.Info, $"Removed empty folder \"{directoryName}\"...");
                                }
                                catch
                                {
                                    // Failure is common case, nothing to report
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to delete file \"{file}\": {ex.Message}", ex));
                        }
                    }
                }

                if (deletedCount > 0)
                    OnStatusMessage(MessageLevel.Info, $"Deleted {deletedCount} files during local file age limit processing.");
                else
                    OnStatusMessage(MessageLevel.Info, "No files deleted during local file age limit processing.");
            }
            catch (Exception ex)
            {
                OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to enumerate local files in \"{settings.LocalPath}\": {ex.Message}", ex));
            }
        }

        private void FtpClient_CommandSent(object sender, EventArgs<string> e)
        {
            if (LogConnectionMessages)
                OnStatusMessage(MessageLevel.Info, $"FTP Request: {e.Argument}");
        }

        private void FtpClient_ResponseReceived(object sender, EventArgs<string> e)
        {
            if (LogConnectionMessages)
                OnStatusMessage(MessageLevel.Info, $"FTP Response: {e.Argument}");
        }

        private void FtpClient_FileTransferProgress(object sender, EventArgs<ProcessProgress<long>, TransferDirection> e)
        {
            ProcessProgress<long> progress = e.Argument1;
            OnProgressUpdated(this, new ProgressUpdate(ProgressState.Processing, false, progress.ProgressMessage, progress.Complete, progress.Total));
        }

        private void FtpClient_FileTransferNotification(object sender, EventArgs<FtpAsyncResult> e)
        {
            OnStatusMessage(MessageLevel.Info, $"FTP File Transfer: {e.Argument.Message}, response code = {e.Argument.ResponseCode}");
        }

        private bool ConnectDialUp()
        {
            if (!UseDialUp)
                return false;

            m_startDialUpTime = 0;
            DisconnectDialUp();

            try
            {
                if (RasState == RasConnectionState.Connected)
                    throw new InvalidOperationException($"Cannot connect to \"{DialUpEntryName}\": already connected.");

                OnStatusMessage(MessageLevel.Info, $"Initiating dial-up for \"{DialUpEntryName}\"...");
                AttemptedDialUps++;

                m_rasDialer.EntryName = DialUpEntryName;
                m_rasDialer.PhoneNumber = DialUpPassword;
                m_rasDialer.Timeout = DialUpTimeout;
                m_rasDialer.Credentials = new NetworkCredential(DialUpUserName, DialUpPassword);
                m_rasDialer.Dial();

                m_startDialUpTime = DateTime.UtcNow.Ticks;
                SuccessfulDialUps++;
                OnStatusMessage(MessageLevel.Info, $"Dial-up connected on \"{DialUpEntryName}\"");
                return true;
            }
            catch (Exception ex)
            {
                FailedDialUps++;
                OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Exception while attempting to dial entry \"{DialUpEntryName}\": {ex.Message}", ex));
                DisconnectDialUp();
            }

            return false;
        }

        private void DisconnectDialUp()
        {
            if (!UseDialUp)
                return;

            try
            {
                OnStatusMessage(MessageLevel.Info, $"Initiating hang-up for \"{DialUpEntryName}\"");
                RasConnection.GetActiveConnections().FirstOrDefault(ras => ras.EntryName == DialUpEntryName)?.HangUp();
            }
            catch (Exception ex)
            {
                OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Exception while attempting to hang-up \"{DialUpEntryName}\": {ex.Message}", ex));
            }

            if (m_startDialUpTime > 0)
            {
                Ticks dialUpConnectedTime = DateTime.UtcNow.Ticks - m_startDialUpTime;
                OnStatusMessage(MessageLevel.Info, $"Dial-up connected for {dialUpConnectedTime.ToElapsedTimeString(2)}");
                m_startDialUpTime = 0;
                TotalDialUpTime += dialUpConnectedTime;
            }
        }

        private void m_rasDialer_Error(object sender, ErrorEventArgs e)
        {
            OnProcessException(MessageLevel.Warning, e.GetException());
        }

        private bool TryUpdateStatusLogTable(FtpFile file, string localFileName, bool success, string message = null)
        {
            try
            {
                UpdateStatusLogTable(file, localFileName, success, message);
                return true;
            }
            catch (Exception ex)
            {
                OnProcessException(MessageLevel.Warning, new Exception($"Failed to update status log due to exception: {ex.Message}", ex));
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void UpdateStatusLogTable(FtpFile file, string localFileName, bool success, string message = null)
        {
            if (!m_deviceRecord.Enabled)
                return;

            try
            {
                using (AdoDataConnection connection = new AdoDataConnection("systemSettings"))
                {
                    TableOperations<StatusLog> statusLogTable = new TableOperations<StatusLog>(connection);
                    TableOperations<DownloadedFile> downloadedFileTable = new TableOperations<DownloadedFile>(connection);
                    StatusLog log = statusLogTable.QueryRecordWhere("DeviceID = {0}", m_deviceRecord.ID) ?? statusLogTable.NewRecord();

                    if (!success)
                    {
                        log.Message = message;
                        log.LastFailure = DateTime.UtcNow;
                    }
                    else if (s_statusLogInclusions.Any(extension => file.Name.EndsWith(extension)) && !s_statusLogExclusions.Any(exclusion => file.Name.EndsWith(exclusion)))
                    {
                        log.LastFile = file.Name;
                        log.FileDownloadTimestamp = log.LastSuccess;

                        downloadedFileTable.AddNewRecord(new DownloadedFile()
                        {
                            DeviceID = m_deviceRecord.ID,
                            CreationTime = new FileInfo(localFileName).CreationTimeUtc,
                            File = file.Name,
                            FileSize = (int)(new FileInfo(localFileName).Length / 1028), // FileSize in KB
                            Timestamp = log.LastSuccess.GetValueOrDefault()
                        });
                    }

                    if (log.DeviceID != m_deviceRecord.ID)
                    {
                        log.DeviceID = m_deviceRecord.ID;
                        statusLogTable.AddNewRecord(log);
                    }
                    else
                    {
                        statusLogTable.UpdateRecord(log);
                    }
                }
            }
            catch (Exception ex)
            {
                OnProcessException(MessageLevel.Warning, new InvalidOperationException($"Failed to update StatusLog database for device \"{m_deviceRecord.Acronym}\": {ex.Message}"));
            }
        }

        #endregion

        #region [ Static ]

        // Static Fields
        private static readonly ScheduleManager s_scheduleManager;
        private static readonly ConcurrentDictionary<string, Downloader> s_instances;
        private static readonly ConcurrentDictionary<string, LogicalThread> s_dialupScheduler;
        private static readonly LogicalThreadScheduler s_logicalThreadScheduler;
        private static readonly int s_ftpThreadCount;
        private static readonly int s_maxDownloadThreshold;
        private static readonly int s_maxDownloadThresholdTimeWindow;
        private static readonly string[] s_statusLogInclusions;
        private static readonly string[] s_statusLogExclusions;

        // Static Events

        /// <summary>
        /// Raised when there is a file transfer progress notification for any downloader instance.
        /// </summary>
        public static event EventHandler<EventArgs<ProgressUpdate>> ProgressUpdated;

        // Static Constructor
        static Downloader()
        {
            const int DefaultFTPThreadCount = 20;
            const int DefaultMaxDownloadThreshold = 0;
            const int DefaultMaxDownloadThresholdTimeWindow = 24;
            const string DefaultStatusLogInclusions = ".rcd,.d00,.dat,.ctl,.cfg,.pcd";
            const string DefaultStatusLogExclusions = "rms.,trend.";

            CategorizedSettingsElementCollection systemSettings = ConfigurationFile.Current.Settings["systemSettings"];
            systemSettings.Add("FTPThreadCount", DefaultFTPThreadCount, "Max thread count for FTP operations. Set to zero for no limit.");
            systemSettings.Add("MaxDownloadThreshold", DefaultMaxDownloadThreshold, "Maximum downloads a meter can have in a specified time range before disabling the meter, subject to specified StatusLog inclusions and exclusions. Set to 0 to disable.");
            systemSettings.Add("MaxDownloadThresholdTimeWindow", DefaultMaxDownloadThresholdTimeWindow, "Time window for the MaxDownloadThreshold in hours.");
            systemSettings.Add("StatusLogInclusions", DefaultStatusLogInclusions, "Default inclusions to apply when writing updates to StatusLog table and checking MaxDownloadThreshold.");
            systemSettings.Add("StatusLogExclusions", DefaultStatusLogExclusions, "Default exclusions to apply when writing updates to StatusLog table and checking MaxDownloadThreshold.");

            s_instances = new ConcurrentDictionary<string, Downloader>();
            s_dialupScheduler = new ConcurrentDictionary<string, LogicalThread>();

            s_ftpThreadCount = systemSettings["FTPThreadCount"].ValueAsInt32(DefaultFTPThreadCount);
            s_logicalThreadScheduler = new LogicalThreadScheduler();
            s_logicalThreadScheduler.MaxThreadCount = s_ftpThreadCount;

            s_scheduleManager = new ScheduleManager();
            s_scheduleManager.ScheduleDue += s_scheduleManager_ScheduleDue;
            s_scheduleManager.Start();

            s_maxDownloadThreshold = systemSettings["MaxDownloadThreshold"].ValueAsInt32(DefaultMaxDownloadThreshold);
            s_maxDownloadThresholdTimeWindow = systemSettings["MaxDownloadThresholdTimeWindow"].ValueAsInt32(DefaultMaxDownloadThresholdTimeWindow);
            s_statusLogInclusions = systemSettings["StatusLogInclusions"].ValueAs(DefaultStatusLogInclusions).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            s_statusLogExclusions = systemSettings["StatusLogExclusions"].ValueAs(DefaultStatusLogExclusions).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static void s_scheduleManager_ScheduleDue(object sender, EventArgs<Schedule> e)
        {
            Schedule schedule = e.Argument;
            Downloader instance;

            if (s_instances.TryGetValue(schedule.Name, out instance))
            {
                instance.AttemptedConnections++;

                if (instance.UseDialUp)
                    instance.m_dialUpOperation.RunOnceAsync();
                else if(instance.UseLogicalThread)
                    instance.m_ftpOperation.RunOnceAsync();
                else
                    instance.m_executeTasks.RunOnceAsync();
            }
        }

        // Static Methods

        private static void RegisterSchedule(Downloader instance)
        {
            s_instances.TryAdd(instance.Name, instance);

            if (instance.UseDialUp)
            {
                // Make sure dial-up's using the same resource (i.e., modem) are executed synchronously
                LogicalThread thread = s_dialupScheduler.GetOrAdd(instance.DialUpEntryName, entryName => new LogicalThread(2));
                WeakReference<Downloader> reference = new WeakReference<Downloader>(instance);

                thread.UnhandledException += (sender, e) =>
                {
                    Downloader downloader;
                    if (reference.TryGetTarget(out downloader))
                        downloader.OnProcessException(MessageLevel.Warning, e.Argument);
                };

                instance.m_dialUpOperation = new LogicalThreadOperation(thread, () =>
                {
                    if (instance.ConnectDialUp())
                    {
                        instance.ExecuteTasks();
                        instance.DisconnectDialUp();
                    }
                }, NormalPriorty);
            }
            else if (s_logicalThreadScheduler.MaxThreadCount > 0)
            {
                LogicalThread thread = s_logicalThreadScheduler.CreateThread();
                thread.UnhandledException += (sender, e) =>
                {
                    WeakReference<Downloader> reference = new WeakReference<Downloader>(instance);
                    Downloader downloader;
                    if (reference.TryGetTarget(out downloader))
                        downloader.OnProcessException(MessageLevel.Warning, e.Argument);
                };

                instance.m_ftpOperation = new LogicalThreadOperation(thread, instance.ExecuteTasks);
            }
            else
            {
                instance.m_executeTasks = new LongSynchronizedOperation(instance.ExecuteTasks, exception => instance.OnProcessException(MessageLevel.Warning, exception));
            }

            s_scheduleManager.AddSchedule(instance.Name, instance.Schedule, $"Download schedule for \"{instance.Name}\"", true);
        }

        private static void DeregisterSchedule(Downloader instance)
        {
            s_scheduleManager.RemoveSchedule(instance.Name);
            s_instances.TryRemove(instance.Name, out instance);
        }

        private static void OnProgressUpdated(Downloader instance, ProgressUpdate update)
        {
            ProgressUpdated?.Invoke(instance, new EventArgs<ProgressUpdate>(update));
        }

        private static void TerminateProcessTree(int ancestorID)
        {
            try
            {
                ManagementObjectSearcher searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_Process WHERE ParentProcessID = {ancestorID}");
                ManagementObjectCollection descendantIDs = searcher.Get();

                foreach (ManagementBaseObject managementObject in descendantIDs)
                {
                    ManagementObject descendantID = managementObject as ManagementObject;

                    if ((object)descendantID != null)
                        TerminateProcessTree(Convert.ToInt32(descendantID["ProcessID"]));
                }

                try
                {
                    using (Process ancestor = Process.GetProcessById(ancestorID))
                        ancestor.Kill();
                }
                catch (ArgumentException)
                {
                    // Process already exited
                }
            }
            catch (Exception ex)
            {
                Program.Host.LogException(new InvalidOperationException($"Failed while attempting to terminate process tree with ancestor ID {ancestorID}: {ex.Message}", ex));
            }
        }

        #region [ Statistic Functions ]

        private static double GetDownloaderStatistic_Enabled(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.IsConnected ? 1.0D : 0.0D;

            return statistic;
        }

        private static double GetDownloaderStatistic_AttemptedConnections(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.AttemptedConnections;

            return statistic;
        }

        private static double GetDownloaderStatistic_SuccessfulConnections(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.SuccessfulConnections;

            return statistic;
        }

        private static double GetDownloaderStatistic_FailedConnections(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.FailedConnections;

            return statistic;
        }

        private static double GetDownloaderStatistic_AttemptedDialUps(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.AttemptedDialUps;

            return statistic;
        }

        private static double GetDownloaderStatistic_SuccessfulDialUps(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.SuccessfulDialUps;

            return statistic;
        }

        private static double GetDownloaderStatistic_FailedDialUps(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.FailedDialUps;

            return statistic;
        }

        private static double GetDownloaderStatistic_FilesDownloaded(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.FilesDownloaded;

            return statistic;
        }

        private static double GetDownloaderStatistic_MegaBytesDownloaded(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = downloader.BytesDownloaded / (double)SI2.Mega;

            return statistic;
        }

        private static double GetDownloaderStatistic_TotalConnectedTime(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = ((Ticks)downloader.TotalConnectedTime).ToSeconds();

            return statistic;
        }

        private static double GetDownloaderStatistic_TotalDialUpTime(object source, string arguments)
        {
            double statistic = 0.0D;
            Downloader downloader = source as Downloader;

            if ((object)downloader != null)
                statistic = ((Ticks)downloader.TotalDialUpTime).ToSeconds();

            return statistic;
        }

        #endregion

        #endregion
    }
}
