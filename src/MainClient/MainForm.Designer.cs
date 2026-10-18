namespace TrafficVista
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            refreshTimer = new System.Windows.Forms.Timer(components);
            saveSettingsTimer = new System.Windows.Forms.Timer(components);
            label_Status = new Label();
            statusMetricsPanel = new Panel();
            liveLogView = new TrafficVista.UiLogging.LiveLogView();
            btnStartStop = new Button();
            numericUpDown_Multiple = new NumericUpDown();
            label110 = new Label();
            textBox_TaskName = new TextBox();
            label4 = new Label();
            label36 = new Label();
            textBox_PageLoadingDelay = new TextBox();
            label35 = new Label();
            label28 = new Label();
            label29 = new Label();
            numericUpDown_PageLoadingTimeout = new NumericUpDown();
            checkBox_VerboseLog = new CheckBox();
            checkBox_IsHiddenMode = new CheckBox();
            checkBox_TaskIntakeBackpressure = new CheckBox();
            numericUpDown_AutoRestartMinutes = new NumericUpDown();
            label_AutoRestart = new Label();
            label_AutoRestartHint = new Label();
            checkBox_IsProxyMode = new CheckBox();
            checkBox_IsCacheMode = new CheckBox();
            groupBox_Cache = new GroupBox();
            comboBox_JavaScriptCacheMode = new ComboBox();
            textBox_CacheExcludedUrlPrefixes = new TextBox();
            label_JsCacheHint = new Label();
            checkBox_RealIp = new CheckBox();
            checkBox_IpDetails = new CheckBox();
            textBox_ProxyIpUrl = new TextBox();
            linkLabel1 = new LinkLabel();
            label1 = new Label();
            label100 = new Label();
            textBox_TaskApiUrl = new TextBox();
            numericUpDown_TaskFetchInterval = new NumericUpDown();
            label2 = new Label();
            label3 = new Label();
            numericUpDown_MaxConcurrency = new NumericUpDown();
            numericUpDown_PlaywrightDriverCount = new NumericUpDown();
            label_PlaywrightDriverCount = new Label();
            label_PlaywrightDriverHint = new Label();
            label27 = new Label();
            panel1 = new Panel();
            linkLabel2 = new LinkLabel();
            label38 = new Label();
            label39 = new Label();
            numericUpDown_IpTtl = new NumericUpDown();
            label_AcquireRetry = new Label();
            numericUpDown_AcquireRetry = new NumericUpDown();
            label_AcquireRetryUnit = new Label();
            numericUpDown_UvExecutionInterval = new NumericUpDown();
            label5 = new Label();
            label6 = new Label();
            textBox_DevApiUrl = new TextBox();
            label14 = new Label();
            statusStrip1 = new StatusStrip();
            toolStripStatusLabel1 = new ToolStripStatusLabel();
            toolStripStatusLabel2 = new ToolStripStatusLabel();
            toolStripStatusLabel3 = new ToolStripStatusLabel();
            toolStripStatusLabel4 = new ToolStripStatusLabel();
            statusMetricsPanel.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_Multiple).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_PageLoadingTimeout).BeginInit();
            groupBox_Cache.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_TaskFetchInterval).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_MaxConcurrency).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_PlaywrightDriverCount).BeginInit();
            panel1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_IpTtl).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_AcquireRetry).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_UvExecutionInterval).BeginInit();
            statusStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // refreshTimer
            // 
            refreshTimer.Interval = 1000;
            // 
            // saveSettingsTimer
            // 
            saveSettingsTimer.Interval = 500;
            // 
            // label_Status
            // 
            label_Status.AutoSize = true;
            label_Status.Location = new Point(0, 0);
            label_Status.Margin = new Padding(4, 0, 4, 0);
            label_Status.Name = "label_Status";
            label_Status.Padding = new Padding(15, 14, 15, 14);
            label_Status.Size = new Size(382, 52);
            label_Status.TabIndex = 0;
            label_Status.Text = "点击启动，按任务提取间隔持续获取任务。";
            // 
            // statusMetricsPanel
            // 
            statusMetricsPanel.AutoScroll = true;
            statusMetricsPanel.BorderStyle = BorderStyle.FixedSingle;
            statusMetricsPanel.Controls.Add(label_Status);
            statusMetricsPanel.Dock = DockStyle.Right;
            statusMetricsPanel.Location = new Point(1011, 0);
            statusMetricsPanel.Margin = new Padding(4);
            statusMetricsPanel.Name = "statusMetricsPanel";
            statusMetricsPanel.Size = new Size(443, 959);
            statusMetricsPanel.TabIndex = 156;
            // 
            // liveLogView
            // 
            liveLogView.Dock = DockStyle.Fill;
            liveLogView.Location = new Point(0, 496);
            liveLogView.Margin = new Padding(4);
            liveLogView.Name = "liveLogView";
            liveLogView.Padding = new Padding(7);
            liveLogView.Size = new Size(1011, 463);
            liveLogView.TabIndex = 155;
            // 
            // btnStartStop
            // 
            btnStartStop.Location = new Point(824, 16);
            btnStartStop.Margin = new Padding(4);
            btnStartStop.Name = "btnStartStop";
            btnStartStop.Size = new Size(150, 112);
            btnStartStop.TabIndex = 0;
            btnStartStop.Text = "启动";
            btnStartStop.UseVisualStyleBackColor = true;
            btnStartStop.Click += btnStartStop_Click;
            // 
            // numericUpDown_Multiple
            // 
            numericUpDown_Multiple.Location = new Point(704, 134);
            numericUpDown_Multiple.Margin = new Padding(6, 5, 6, 5);
            numericUpDown_Multiple.Maximum = new decimal(new int[] { 1000, 0, 0, 0 });
            numericUpDown_Multiple.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_Multiple.Name = "numericUpDown_Multiple";
            numericUpDown_Multiple.Size = new Size(106, 30);
            numericUpDown_Multiple.TabIndex = 126;
            numericUpDown_Multiple.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // label110
            // 
            label110.AutoSize = true;
            label110.Location = new Point(615, 139);
            label110.Margin = new Padding(6, 0, 6, 0);
            label110.Name = "label110";
            label110.Size = new Size(82, 24);
            label110.TabIndex = 125;
            label110.Text = "任务倍速";
            // 
            // textBox_TaskName
            // 
            textBox_TaskName.Location = new Point(159, 134);
            textBox_TaskName.Margin = new Padding(6, 5, 6, 5);
            textBox_TaskName.Name = "textBox_TaskName";
            textBox_TaskName.Size = new Size(220, 30);
            textBox_TaskName.TabIndex = 119;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(70, 139);
            label4.Margin = new Padding(6, 0, 6, 0);
            label4.Name = "label4";
            label4.Size = new Size(82, 24);
            label4.TabIndex = 115;
            label4.Text = "任务名称";
            // 
            // label36
            // 
            label36.AutoSize = true;
            label36.Location = new Point(279, 304);
            label36.Margin = new Padding(6, 0, 6, 0);
            label36.Name = "label36";
            label36.Size = new Size(28, 24);
            label36.TabIndex = 141;
            label36.Text = "秒";
            // 
            // textBox_PageLoadingDelay
            // 
            textBox_PageLoadingDelay.Location = new Point(159, 293);
            textBox_PageLoadingDelay.Margin = new Padding(6, 5, 6, 5);
            textBox_PageLoadingDelay.Name = "textBox_PageLoadingDelay";
            textBox_PageLoadingDelay.Size = new Size(112, 30);
            textBox_PageLoadingDelay.TabIndex = 140;
            // 
            // label35
            // 
            label35.AutoSize = true;
            label35.Location = new Point(33, 298);
            label35.Margin = new Padding(6, 0, 6, 0);
            label35.Name = "label35";
            label35.Size = new Size(118, 24);
            label35.TabIndex = 139;
            label35.Text = "首页加载延时";
            // 
            // label28
            // 
            label28.AutoSize = true;
            label28.Location = new Point(279, 342);
            label28.Margin = new Padding(6, 0, 6, 0);
            label28.Name = "label28";
            label28.Size = new Size(28, 24);
            label28.TabIndex = 138;
            label28.Text = "秒";
            // 
            // label29
            // 
            label29.AutoSize = true;
            label29.Location = new Point(33, 337);
            label29.Margin = new Padding(6, 0, 6, 0);
            label29.Name = "label29";
            label29.Size = new Size(118, 24);
            label29.TabIndex = 136;
            label29.Text = "页面加载超时";
            // 
            // numericUpDown_PageLoadingTimeout
            // 
            numericUpDown_PageLoadingTimeout.Location = new Point(159, 332);
            numericUpDown_PageLoadingTimeout.Margin = new Padding(6, 5, 6, 5);
            numericUpDown_PageLoadingTimeout.Maximum = new decimal(new int[] { 3600, 0, 0, 0 });
            numericUpDown_PageLoadingTimeout.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_PageLoadingTimeout.Name = "numericUpDown_PageLoadingTimeout";
            numericUpDown_PageLoadingTimeout.Size = new Size(112, 30);
            numericUpDown_PageLoadingTimeout.TabIndex = 137;
            numericUpDown_PageLoadingTimeout.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // checkBox_VerboseLog
            // 
            checkBox_VerboseLog.AutoSize = true;
            checkBox_VerboseLog.Location = new Point(863, 214);
            checkBox_VerboseLog.Margin = new Padding(6);
            checkBox_VerboseLog.Name = "checkBox_VerboseLog";
            checkBox_VerboseLog.Size = new Size(108, 28);
            checkBox_VerboseLog.TabIndex = 132;
            checkBox_VerboseLog.Text = "详细日志";
            checkBox_VerboseLog.UseVisualStyleBackColor = true;
            // 
            // checkBox_IsHiddenMode
            // 
            checkBox_IsHiddenMode.AutoSize = true;
            checkBox_IsHiddenMode.Location = new Point(863, 139);
            checkBox_IsHiddenMode.Margin = new Padding(6);
            checkBox_IsHiddenMode.Name = "checkBox_IsHiddenMode";
            checkBox_IsHiddenMode.Size = new Size(108, 28);
            checkBox_IsHiddenMode.TabIndex = 127;
            checkBox_IsHiddenMode.Text = "隐藏模式";
            checkBox_IsHiddenMode.UseVisualStyleBackColor = true;
            // 
            // checkBox_IsProxyMode
            // 
            checkBox_IsProxyMode.AutoSize = true;
            checkBox_IsProxyMode.Location = new Point(863, 176);
            checkBox_IsProxyMode.Margin = new Padding(6);
            checkBox_IsProxyMode.Name = "checkBox_IsProxyMode";
            checkBox_IsProxyMode.Size = new Size(108, 28);
            checkBox_IsProxyMode.TabIndex = 128;
            checkBox_IsProxyMode.Text = "代理模式";
            checkBox_IsProxyMode.UseVisualStyleBackColor = true;
            // 
            // checkBox_IsCacheMode
            // 
            checkBox_IsCacheMode.AutoSize = true;
            checkBox_IsCacheMode.Location = new Point(18, 28);
            checkBox_IsCacheMode.Margin = new Padding(6);
            checkBox_IsCacheMode.Name = "checkBox_IsCacheMode";
            checkBox_IsCacheMode.Size = new Size(108, 28);
            checkBox_IsCacheMode.TabIndex = 134;
            checkBox_IsCacheMode.Text = "启用缓存";
            checkBox_IsCacheMode.UseVisualStyleBackColor = true;
            // 
            // groupBox_Cache
            // 
            groupBox_Cache.Controls.Add(checkBox_IsCacheMode);
            groupBox_Cache.Controls.Add(comboBox_JavaScriptCacheMode);
            groupBox_Cache.Controls.Add(textBox_CacheExcludedUrlPrefixes);
            groupBox_Cache.Controls.Add(label_JsCacheHint);
            groupBox_Cache.Location = new Point(350, 250);
            groupBox_Cache.Name = "groupBox_Cache";
            groupBox_Cache.Size = new Size(340, 235);
            groupBox_Cache.TabIndex = 157;
            groupBox_Cache.TabStop = false;
            groupBox_Cache.Text = "资源缓存";
            // 
            // comboBox_JavaScriptCacheMode
            // 
            comboBox_JavaScriptCacheMode.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox_JavaScriptCacheMode.Items.AddRange(new object[] { "1：JS不缓存", "2：JS URL缓存模式", "3：JS URL＋内容缓存模式" });
            comboBox_JavaScriptCacheMode.Location = new Point(18, 65);
            comboBox_JavaScriptCacheMode.Name = "comboBox_JavaScriptCacheMode";
            comboBox_JavaScriptCacheMode.Size = new Size(304, 32);
            comboBox_JavaScriptCacheMode.TabIndex = 1;
            // 
            // textBox_CacheExcludedUrlPrefixes
            // 
            textBox_CacheExcludedUrlPrefixes.AcceptsReturn = true;
            textBox_CacheExcludedUrlPrefixes.Location = new Point(18, 140);
            textBox_CacheExcludedUrlPrefixes.Multiline = true;
            textBox_CacheExcludedUrlPrefixes.Name = "textBox_CacheExcludedUrlPrefixes";
            textBox_CacheExcludedUrlPrefixes.ScrollBars = ScrollBars.Vertical;
            textBox_CacheExcludedUrlPrefixes.Size = new Size(304, 78);
            textBox_CacheExcludedUrlPrefixes.TabIndex = 2;
            textBox_CacheExcludedUrlPrefixes.WordWrap = false;
            // 
            // label_JsCacheHint
            // 
            label_JsCacheHint.AutoSize = true;
            label_JsCacheHint.ForeColor = SystemColors.GrayText;
            label_JsCacheHint.Location = new Point(18, 108);
            label_JsCacheHint.Name = "label_JsCacheHint";
            label_JsCacheHint.Size = new Size(259, 24);
            label_JsCacheHint.TabIndex = 135;
            label_JsCacheHint.Text = "JS不缓存URL前缀（每行一条）";
            // 
            // checkBox_RealIp
            // 
            checkBox_RealIp.AutoSize = true;
            checkBox_RealIp.Location = new Point(863, 290);
            checkBox_RealIp.Margin = new Padding(4);
            checkBox_RealIp.Name = "checkBox_RealIp";
            checkBox_RealIp.Size = new Size(88, 28);
            checkBox_RealIp.TabIndex = 143;
            checkBox_RealIp.Text = "真实IP";
            checkBox_RealIp.UseVisualStyleBackColor = true;
            // 
            // checkBox_IpDetails
            // 
            checkBox_IpDetails.AutoSize = true;
            checkBox_IpDetails.Location = new Point(863, 330);
            checkBox_IpDetails.Margin = new Padding(4);
            checkBox_IpDetails.Name = "checkBox_IpDetails";
            checkBox_IpDetails.Size = new Size(88, 28);
            checkBox_IpDetails.TabIndex = 144;
            checkBox_IpDetails.Text = "IP详情";
            checkBox_IpDetails.UseVisualStyleBackColor = true;
            //
            // checkBox_TaskIntakeBackpressure
            //
            checkBox_TaskIntakeBackpressure.AutoSize = true;
            checkBox_TaskIntakeBackpressure.Location = new Point(840, 370);
            checkBox_TaskIntakeBackpressure.Name = "checkBox_TaskIntakeBackpressure";
            checkBox_TaskIntakeBackpressure.TabIndex = 158;
            checkBox_TaskIntakeBackpressure.Text = "任务领取背压";
            checkBox_TaskIntakeBackpressure.UseVisualStyleBackColor = true;
            //
            // Periodic graceful restart
            //
            label_AutoRestart.AutoSize = true;
            label_AutoRestart.Location = new Point(704, 411);
            label_AutoRestart.Name = "label_AutoRestart";
            label_AutoRestart.Text = "定时重启";
            numericUpDown_AutoRestartMinutes.Location = new Point(802, 406);
            numericUpDown_AutoRestartMinutes.Maximum = 10080;
            numericUpDown_AutoRestartMinutes.Name = "numericUpDown_AutoRestartMinutes";
            numericUpDown_AutoRestartMinutes.Size = new Size(90, 30);
            numericUpDown_AutoRestartMinutes.TabIndex = 161;
            label_AutoRestartHint.Location = new Point(704, 444);
            label_AutoRestartHint.Size = new Size(290, 46);
            label_AutoRestartHint.Name = "label_AutoRestartHint";
            label_AutoRestartHint.Text = "单位：分钟；0为关闭\n到时暂存队列，等在途任务完成";
            label_AutoRestartHint.ForeColor = SystemColors.GrayText;
            // 
            // textBox_ProxyIpUrl
            // 
            textBox_ProxyIpUrl.Location = new Point(159, 55);
            textBox_ProxyIpUrl.Margin = new Padding(6, 5, 6, 5);
            textBox_ProxyIpUrl.Name = "textBox_ProxyIpUrl";
            textBox_ProxyIpUrl.Size = new Size(654, 30);
            textBox_ProxyIpUrl.TabIndex = 118;
            // 
            // linkLabel1
            // 
            linkLabel1.AutoSize = true;
            linkLabel1.Location = new Point(704, 277);
            linkLabel1.Margin = new Padding(5, 0, 5, 0);
            linkLabel1.Name = "linkLabel1";
            linkLabel1.Size = new Size(82, 24);
            linkLabel1.TabIndex = 133;
            linkLabel1.TabStop = true;
            linkLabel1.Text = "开机启动";
            linkLabel1.LinkClicked += linkLabel1_LinkClicked;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(70, 60);
            label1.Margin = new Padding(6, 0, 6, 0);
            label1.Name = "label1";
            label1.Size = new Size(82, 24);
            label1.TabIndex = 117;
            label1.Text = "代理接口";
            // 
            // label100
            // 
            label100.AutoSize = true;
            label100.Location = new Point(70, 20);
            label100.Margin = new Padding(6, 0, 6, 0);
            label100.Name = "label100";
            label100.Size = new Size(82, 24);
            label100.TabIndex = 123;
            label100.Text = "任务接口";
            // 
            // textBox_TaskApiUrl
            // 
            textBox_TaskApiUrl.Location = new Point(159, 16);
            textBox_TaskApiUrl.Margin = new Padding(6, 5, 6, 5);
            textBox_TaskApiUrl.Name = "textBox_TaskApiUrl";
            textBox_TaskApiUrl.Size = new Size(654, 30);
            textBox_TaskApiUrl.TabIndex = 124;
            // 
            // numericUpDown_TaskFetchInterval
            // 
            numericUpDown_TaskFetchInterval.Location = new Point(159, 174);
            numericUpDown_TaskFetchInterval.Margin = new Padding(6, 5, 6, 5);
            numericUpDown_TaskFetchInterval.Maximum = new decimal(new int[] { 60000, 0, 0, 0 });
            numericUpDown_TaskFetchInterval.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_TaskFetchInterval.Name = "numericUpDown_TaskFetchInterval";
            numericUpDown_TaskFetchInterval.Size = new Size(112, 30);
            numericUpDown_TaskFetchInterval.TabIndex = 122;
            numericUpDown_TaskFetchInterval.Value = new decimal(new int[] { 1000, 0, 0, 0 });
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(33, 179);
            label2.Margin = new Padding(6, 0, 6, 0);
            label2.Name = "label2";
            label2.Size = new Size(118, 24);
            label2.TabIndex = 116;
            label2.Text = "任务提取间隔";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(279, 179);
            label3.Margin = new Padding(6, 0, 6, 0);
            label3.Name = "label3";
            label3.Size = new Size(46, 24);
            label3.TabIndex = 114;
            label3.Text = "毫秒";
            // 
            // numericUpDown_MaxConcurrency
            // 
            numericUpDown_MaxConcurrency.Location = new Point(159, 253);
            numericUpDown_MaxConcurrency.Margin = new Padding(6, 5, 6, 5);
            numericUpDown_MaxConcurrency.Maximum = new decimal(new int[] { 1024, 0, 0, 0 });
            numericUpDown_MaxConcurrency.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_MaxConcurrency.Name = "numericUpDown_MaxConcurrency";
            numericUpDown_MaxConcurrency.Size = new Size(112, 30);
            numericUpDown_MaxConcurrency.TabIndex = 121;
            numericUpDown_MaxConcurrency.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // numericUpDown_PlaywrightDriverCount
            // 
            numericUpDown_PlaywrightDriverCount.Location = new Point(180, 374);
            numericUpDown_PlaywrightDriverCount.Maximum = new decimal(new int[] { 1024, 0, 0, 0 });
            numericUpDown_PlaywrightDriverCount.Name = "numericUpDown_PlaywrightDriverCount";
            numericUpDown_PlaywrightDriverCount.Size = new Size(91, 30);
            numericUpDown_PlaywrightDriverCount.TabIndex = 158;
            numericUpDown_PlaywrightDriverCount.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // label_PlaywrightDriverCount
            // 
            label_PlaywrightDriverCount.AutoSize = true;
            label_PlaywrightDriverCount.Location = new Point(13, 379);
            label_PlaywrightDriverCount.Name = "label_PlaywrightDriverCount";
            label_PlaywrightDriverCount.Size = new Size(156, 24);
            label_PlaywrightDriverCount.TabIndex = 159;
            label_PlaywrightDriverCount.Text = "Playwright驱动数";
            // 
            // label_PlaywrightDriverHint
            // 
            label_PlaywrightDriverHint.Location = new Point(18, 412);
            label_PlaywrightDriverHint.Name = "label_PlaywrightDriverHint";
            label_PlaywrightDriverHint.Size = new Size(310, 74);
            label_PlaywrightDriverHint.TabIndex = 160;
            label_PlaywrightDriverHint.Text = "0：跟随任务并发\n1：共用驱动；大于1：指定数量\n按需创建，下一轮启动生效";
            // 
            // label27
            // 
            label27.AutoSize = true;
            label27.Location = new Point(45, 257);
            label27.Margin = new Padding(6, 0, 6, 0);
            label27.Name = "label27";
            label27.Size = new Size(100, 24);
            label27.TabIndex = 120;
            label27.Text = "任务并发数";
            // 
            // panel1
            // 
            panel1.Controls.Add(linkLabel2);
            panel1.Controls.Add(label38);
            panel1.Controls.Add(label39);
            panel1.Controls.Add(numericUpDown_IpTtl);
            panel1.Controls.Add(label_AcquireRetry);
            panel1.Controls.Add(numericUpDown_AcquireRetry);
            panel1.Controls.Add(label_AcquireRetryUnit);
            panel1.Controls.Add(numericUpDown_UvExecutionInterval);
            panel1.Controls.Add(label5);
            panel1.Controls.Add(label6);
            panel1.Controls.Add(textBox_DevApiUrl);
            panel1.Controls.Add(btnStartStop);
            panel1.Controls.Add(label14);
            panel1.Controls.Add(textBox_TaskApiUrl);
            panel1.Controls.Add(numericUpDown_Multiple);
            panel1.Controls.Add(label27);
            panel1.Controls.Add(label110);
            panel1.Controls.Add(numericUpDown_MaxConcurrency);
            panel1.Controls.Add(numericUpDown_PlaywrightDriverCount);
            panel1.Controls.Add(label_PlaywrightDriverCount);
            panel1.Controls.Add(label_PlaywrightDriverHint);
            panel1.Controls.Add(textBox_TaskName);
            panel1.Controls.Add(label3);
            panel1.Controls.Add(label4);
            panel1.Controls.Add(label2);
            panel1.Controls.Add(label36);
            panel1.Controls.Add(numericUpDown_TaskFetchInterval);
            panel1.Controls.Add(textBox_PageLoadingDelay);
            panel1.Controls.Add(label35);
            panel1.Controls.Add(label28);
            panel1.Controls.Add(label29);
            panel1.Controls.Add(numericUpDown_PageLoadingTimeout);
            panel1.Controls.Add(checkBox_VerboseLog);
            panel1.Controls.Add(label100);
            panel1.Controls.Add(checkBox_IsHiddenMode);
            panel1.Controls.Add(label1);
            panel1.Controls.Add(checkBox_IsProxyMode);
            panel1.Controls.Add(groupBox_Cache);
            panel1.Controls.Add(checkBox_RealIp);
            panel1.Controls.Add(checkBox_IpDetails);
            panel1.Controls.Add(checkBox_TaskIntakeBackpressure);
            panel1.Controls.Add(numericUpDown_AutoRestartMinutes);
            panel1.Controls.Add(label_AutoRestart);
            panel1.Controls.Add(label_AutoRestartHint);
            panel1.Controls.Add(linkLabel1);
            panel1.Controls.Add(textBox_ProxyIpUrl);
            panel1.Dock = DockStyle.Top;
            panel1.Location = new Point(0, 0);
            panel1.Margin = new Padding(4);
            panel1.Name = "panel1";
            panel1.Size = new Size(1011, 496);
            panel1.TabIndex = 142;
            panel1.Paint += panel1_Paint;
            // 
            // linkLabel2
            // 
            linkLabel2.AutoSize = true;
            linkLabel2.Location = new Point(704, 316);
            linkLabel2.Margin = new Padding(6, 0, 6, 0);
            linkLabel2.Name = "linkLabel2";
            linkLabel2.Size = new Size(82, 24);
            linkLabel2.TabIndex = 150;
            linkLabel2.TabStop = true;
            linkLabel2.Text = "应用目录";
            // 
            // label38
            // 
            label38.AutoSize = true;
            label38.Location = new Point(788, 179);
            label38.Margin = new Padding(7, 0, 7, 0);
            label38.Name = "label38";
            label38.Size = new Size(28, 24);
            label38.TabIndex = 149;
            label38.Text = "秒";
            // 
            // label39
            // 
            label39.AutoSize = true;
            label39.Location = new Point(599, 179);
            label39.Margin = new Padding(7, 0, 7, 0);
            label39.Name = "label39";
            label39.Size = new Size(98, 24);
            label39.TabIndex = 147;
            label39.Text = "IP有效时长";
            // 
            // numericUpDown_IpTtl
            // 
            numericUpDown_IpTtl.Location = new Point(704, 174);
            numericUpDown_IpTtl.Margin = new Padding(7, 6, 7, 6);
            numericUpDown_IpTtl.Maximum = new decimal(new int[] { 1800, 0, 0, 0 });
            numericUpDown_IpTtl.Name = "numericUpDown_IpTtl";
            numericUpDown_IpTtl.Size = new Size(81, 30);
            numericUpDown_IpTtl.TabIndex = 148;
            // 
            // label_AcquireRetry
            // 
            label_AcquireRetry.AutoSize = true;
            label_AcquireRetry.Location = new Point(562, 218);
            label_AcquireRetry.Margin = new Padding(7, 0, 7, 0);
            label_AcquireRetry.Name = "label_AcquireRetry";
            label_AcquireRetry.Size = new Size(134, 24);
            label_AcquireRetry.TabIndex = 151;
            label_AcquireRetry.Text = "IP获取重试次数";
            // 
            // numericUpDown_AcquireRetry
            // 
            numericUpDown_AcquireRetry.Location = new Point(704, 214);
            numericUpDown_AcquireRetry.Margin = new Padding(7, 6, 7, 6);
            numericUpDown_AcquireRetry.Maximum = new decimal(new int[] { 10, 0, 0, 0 });
            numericUpDown_AcquireRetry.Name = "numericUpDown_AcquireRetry";
            numericUpDown_AcquireRetry.Size = new Size(81, 30);
            numericUpDown_AcquireRetry.TabIndex = 152;
            // 
            // label_AcquireRetryUnit
            // 
            label_AcquireRetryUnit.AutoSize = true;
            label_AcquireRetryUnit.Location = new Point(788, 218);
            label_AcquireRetryUnit.Margin = new Padding(7, 0, 7, 0);
            label_AcquireRetryUnit.Name = "label_AcquireRetryUnit";
            label_AcquireRetryUnit.Size = new Size(28, 24);
            label_AcquireRetryUnit.TabIndex = 153;
            label_AcquireRetryUnit.Text = "次";
            // 
            // numericUpDown_UvExecutionInterval
            // 
            numericUpDown_UvExecutionInterval.Location = new Point(159, 214);
            numericUpDown_UvExecutionInterval.Margin = new Padding(7, 6, 7, 6);
            numericUpDown_UvExecutionInterval.Maximum = new decimal(new int[] { 60000, 0, 0, 0 });
            numericUpDown_UvExecutionInterval.Name = "numericUpDown_UvExecutionInterval";
            numericUpDown_UvExecutionInterval.Size = new Size(110, 30);
            numericUpDown_UvExecutionInterval.TabIndex = 146;
            numericUpDown_UvExecutionInterval.Value = new decimal(new int[] { 1000, 0, 0, 0 });
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(44, 218);
            label5.Margin = new Padding(7, 0, 7, 0);
            label5.Name = "label5";
            label5.Size = new Size(107, 24);
            label5.TabIndex = 144;
            label5.Text = "UV执行间隔";
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new Point(276, 218);
            label6.Margin = new Padding(7, 0, 7, 0);
            label6.Name = "label6";
            label6.Size = new Size(46, 24);
            label6.TabIndex = 145;
            label6.Text = "毫秒";
            // 
            // textBox_DevApiUrl
            // 
            textBox_DevApiUrl.Location = new Point(159, 95);
            textBox_DevApiUrl.Margin = new Padding(6, 5, 6, 5);
            textBox_DevApiUrl.Name = "textBox_DevApiUrl";
            textBox_DevApiUrl.Size = new Size(654, 30);
            textBox_DevApiUrl.TabIndex = 143;
            textBox_DevApiUrl.Text = "http://117.21.200.18:9000/api/getdev.php";
            // 
            // label14
            // 
            label14.AutoSize = true;
            label14.Location = new Point(70, 98);
            label14.Margin = new Padding(6, 0, 6, 0);
            label14.Name = "label14";
            label14.Size = new Size(82, 24);
            label14.TabIndex = 142;
            label14.Text = "设备接口";
            // 
            // statusStrip1
            // 
            statusStrip1.ImageScalingSize = new Size(20, 20);
            statusStrip1.Items.AddRange(new ToolStripItem[] { toolStripStatusLabel1, toolStripStatusLabel2, toolStripStatusLabel3, toolStripStatusLabel4 });
            statusStrip1.Location = new Point(0, 959);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Padding = new Padding(1, 0, 17, 0);
            statusStrip1.Size = new Size(1454, 31);
            statusStrip1.TabIndex = 156;
            statusStrip1.Text = "statusStrip1";
            // 
            // toolStripStatusLabel1
            // 
            toolStripStatusLabel1.Name = "toolStripStatusLabel1";
            toolStripStatusLabel1.Size = new Size(118, 24);
            toolStripStatusLabel1.Text = "应用：待启动";
            // 
            // toolStripStatusLabel2
            // 
            toolStripStatusLabel2.Name = "toolStripStatusLabel2";
            toolStripStatusLabel2.Size = new Size(118, 24);
            toolStripStatusLabel2.Text = "CPU：采样中";
            // 
            // toolStripStatusLabel3
            // 
            toolStripStatusLabel3.Name = "toolStripStatusLabel3";
            toolStripStatusLabel3.Size = new Size(118, 24);
            toolStripStatusLabel3.Text = "内存：采样中";
            // 
            // toolStripStatusLabel4
            // 
            toolStripStatusLabel4.Name = "toolStripStatusLabel4";
            toolStripStatusLabel4.Size = new Size(172, 24);
            toolStripStatusLabel4.Text = "浏览器进程：采样中";
            // 
            // MainForm
            // 
            // Original designer coordinates were authored at 150% (144 DPI).
            AutoScaleDimensions = new SizeF(144F, 144F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1454, 990);
            Controls.Add(liveLogView);
            Controls.Add(panel1);
            Controls.Add(statusMetricsPanel);
            Controls.Add(statusStrip1);
            Margin = new Padding(4);
            MinimumSize = new Size(1034, 589);
            Name = "MainForm";
            Text = "TrafficVista(v20261014-1)";
            Load += MainForm_Load;
            statusMetricsPanel.ResumeLayout(false);
            statusMetricsPanel.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_Multiple).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_PageLoadingTimeout).EndInit();
            groupBox_Cache.ResumeLayout(false);
            groupBox_Cache.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_TaskFetchInterval).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_MaxConcurrency).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_PlaywrightDriverCount).EndInit();
            panel1.ResumeLayout(false);
            panel1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_IpTtl).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_AcquireRetry).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_UvExecutionInterval).EndInit();
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button btnStartStop;
        private Panel statusMetricsPanel;
        private NumericUpDown numericUpDown_Multiple;
        private Label label110;
        private TextBox textBox_TaskName;
        private Label label4;
        private Label label36;
        private TextBox textBox_PageLoadingDelay;
        private Label label35;
        private Label label28;
        private Label label29;
        private NumericUpDown numericUpDown_PageLoadingTimeout;
        private CheckBox checkBox_VerboseLog;
        private CheckBox checkBox_IsHiddenMode;
        private CheckBox checkBox_IsProxyMode;
        private CheckBox checkBox_IsCacheMode;
        private GroupBox groupBox_Cache;
        private ComboBox comboBox_JavaScriptCacheMode;
        private TextBox textBox_CacheExcludedUrlPrefixes;
        private Label label_JsCacheHint;
        private CheckBox checkBox_RealIp;
        private CheckBox checkBox_IpDetails;
        private TextBox textBox_ProxyIpUrl;
        private LinkLabel linkLabel1;
        private Label label1;
        private Label label100;
        private TextBox textBox_TaskApiUrl;
        private NumericUpDown numericUpDown_TaskFetchInterval;
        private Label label2;
        private Label label3;
        private NumericUpDown numericUpDown_MaxConcurrency;
        private NumericUpDown numericUpDown_PlaywrightDriverCount;
        private Label label_PlaywrightDriverCount;
        private Label label_PlaywrightDriverHint;
        private Label label27;
        private Panel panel1;
        private TextBox textBox_DevApiUrl;
        private Label label14;
        private NumericUpDown numericUpDown_UvExecutionInterval;
        private Label label5;
        private Label label6;
        private Label label38;
        private Label label39;
        private NumericUpDown numericUpDown_IpTtl;
        private Label label_AcquireRetry;
        private NumericUpDown numericUpDown_AcquireRetry;
        private Label label_AcquireRetryUnit;
        private LinkLabel linkLabel2;
        private Label label_Status;
        private CheckBox checkBox_TaskIntakeBackpressure;
        private NumericUpDown numericUpDown_AutoRestartMinutes;
        private Label label_AutoRestart, label_AutoRestartHint;
        private UiLogging.LiveLogView liveLogView;
        private System.Windows.Forms.Timer refreshTimer;
        private System.Windows.Forms.Timer saveSettingsTimer;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel toolStripStatusLabel1;
        private ToolStripStatusLabel toolStripStatusLabel2;
        private ToolStripStatusLabel toolStripStatusLabel3;
        private ToolStripStatusLabel toolStripStatusLabel4;
    }
}
