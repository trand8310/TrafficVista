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
            checkBox_IsProxyMode = new CheckBox();
            checkBox_IsCacheMode = new CheckBox();
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
            numericUpDown_TaskWorkers = new NumericUpDown();
            label_TaskWorkers = new Label();
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
            ((System.ComponentModel.ISupportInitialize)numericUpDown_Multiple).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_PageLoadingTimeout).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_TaskFetchInterval).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_MaxConcurrency).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_TaskWorkers).BeginInit();
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
            label_Status.AutoEllipsis = true;
            label_Status.Location = new Point(815, 9);
            label_Status.Name = "label_Status";
            label_Status.Padding = new Padding(12);
            label_Status.Size = new Size(363, 193);
            label_Status.TabIndex = 0;
            label_Status.Text = "点击启动，按任务提取间隔持续获取任务。";
            // 
            // liveLogView
            // 
            liveLogView.Dock = DockStyle.Fill;
            liveLogView.Location = new Point(0, 327);
            liveLogView.Name = "liveLogView";
            liveLogView.Padding = new Padding(6);
            liveLogView.Size = new Size(1190, 553);
            liveLogView.TabIndex = 155;
            // 
            // btnStartStop
            // 
            btnStartStop.Location = new Point(674, 13);
            btnStartStop.Name = "btnStartStop";
            btnStartStop.Size = new Size(123, 93);
            btnStartStop.TabIndex = 0;
            btnStartStop.Text = "启动";
            btnStartStop.UseVisualStyleBackColor = true;
            btnStartStop.Click += btnStartStop_Click;
            // 
            // numericUpDown_Multiple
            // 
            numericUpDown_Multiple.Location = new Point(576, 112);
            numericUpDown_Multiple.Margin = new Padding(5, 4, 5, 4);
            numericUpDown_Multiple.Maximum = new decimal(new int[] { 1000, 0, 0, 0 });
            numericUpDown_Multiple.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_Multiple.Name = "numericUpDown_Multiple";
            numericUpDown_Multiple.Size = new Size(87, 27);
            numericUpDown_Multiple.TabIndex = 126;
            numericUpDown_Multiple.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // label110
            // 
            label110.AutoSize = true;
            label110.Location = new Point(503, 116);
            label110.Margin = new Padding(5, 0, 5, 0);
            label110.Name = "label110";
            label110.Size = new Size(69, 20);
            label110.TabIndex = 125;
            label110.Text = "任务倍速";
            // 
            // textBox_TaskName
            // 
            textBox_TaskName.Location = new Point(130, 112);
            textBox_TaskName.Margin = new Padding(5, 4, 5, 4);
            textBox_TaskName.Name = "textBox_TaskName";
            textBox_TaskName.Size = new Size(181, 27);
            textBox_TaskName.TabIndex = 119;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(57, 116);
            label4.Margin = new Padding(5, 0, 5, 0);
            label4.Name = "label4";
            label4.Size = new Size(69, 20);
            label4.TabIndex = 115;
            label4.Text = "任务名称";
            // 
            // label36
            // 
            label36.AutoSize = true;
            label36.Location = new Point(228, 253);
            label36.Margin = new Padding(5, 0, 5, 0);
            label36.Name = "label36";
            label36.Size = new Size(24, 20);
            label36.TabIndex = 141;
            label36.Text = "秒";
            // 
            // textBox_PageLoadingDelay
            // 
            textBox_PageLoadingDelay.Location = new Point(130, 244);
            textBox_PageLoadingDelay.Margin = new Padding(5, 4, 5, 4);
            textBox_PageLoadingDelay.Name = "textBox_PageLoadingDelay";
            textBox_PageLoadingDelay.Size = new Size(92, 27);
            textBox_PageLoadingDelay.TabIndex = 140;
            // 
            // label35
            // 
            label35.AutoSize = true;
            label35.Location = new Point(27, 248);
            label35.Margin = new Padding(5, 0, 5, 0);
            label35.Name = "label35";
            label35.Size = new Size(99, 20);
            label35.TabIndex = 139;
            label35.Text = "首页加载延时";
            // 
            // label28
            // 
            label28.AutoSize = true;
            label28.Location = new Point(228, 285);
            label28.Margin = new Padding(5, 0, 5, 0);
            label28.Name = "label28";
            label28.Size = new Size(24, 20);
            label28.TabIndex = 138;
            label28.Text = "秒";
            // 
            // label29
            // 
            label29.AutoSize = true;
            label29.Location = new Point(27, 281);
            label29.Margin = new Padding(5, 0, 5, 0);
            label29.Name = "label29";
            label29.Size = new Size(99, 20);
            label29.TabIndex = 136;
            label29.Text = "页面加载超时";
            // 
            // numericUpDown_PageLoadingTimeout
            // 
            numericUpDown_PageLoadingTimeout.Location = new Point(130, 277);
            numericUpDown_PageLoadingTimeout.Margin = new Padding(5, 4, 5, 4);
            numericUpDown_PageLoadingTimeout.Maximum = new decimal(new int[] { 3600, 0, 0, 0 });
            numericUpDown_PageLoadingTimeout.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_PageLoadingTimeout.Name = "numericUpDown_PageLoadingTimeout";
            numericUpDown_PageLoadingTimeout.Size = new Size(92, 27);
            numericUpDown_PageLoadingTimeout.TabIndex = 137;
            numericUpDown_PageLoadingTimeout.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // checkBox_VerboseLog
            // 
            checkBox_VerboseLog.AutoSize = true;
            checkBox_VerboseLog.Location = new Point(706, 178);
            checkBox_VerboseLog.Margin = new Padding(5);
            checkBox_VerboseLog.Name = "checkBox_VerboseLog";
            checkBox_VerboseLog.Size = new Size(91, 24);
            checkBox_VerboseLog.TabIndex = 132;
            checkBox_VerboseLog.Text = "详细日志";
            checkBox_VerboseLog.UseVisualStyleBackColor = true;
            // 
            // checkBox_IsHiddenMode
            // 
            checkBox_IsHiddenMode.AutoSize = true;
            checkBox_IsHiddenMode.Location = new Point(706, 116);
            checkBox_IsHiddenMode.Margin = new Padding(5);
            checkBox_IsHiddenMode.Name = "checkBox_IsHiddenMode";
            checkBox_IsHiddenMode.Size = new Size(91, 24);
            checkBox_IsHiddenMode.TabIndex = 127;
            checkBox_IsHiddenMode.Text = "隐藏模式";
            checkBox_IsHiddenMode.UseVisualStyleBackColor = true;
            // 
            // checkBox_IsProxyMode
            // 
            checkBox_IsProxyMode.AutoSize = true;
            checkBox_IsProxyMode.Location = new Point(706, 147);
            checkBox_IsProxyMode.Margin = new Padding(5);
            checkBox_IsProxyMode.Name = "checkBox_IsProxyMode";
            checkBox_IsProxyMode.Size = new Size(91, 24);
            checkBox_IsProxyMode.TabIndex = 128;
            checkBox_IsProxyMode.Text = "代理模式";
            checkBox_IsProxyMode.UseVisualStyleBackColor = true;
            // 
            // checkBox_IsCacheMode
            // 
            checkBox_IsCacheMode.AutoSize = true;
            checkBox_IsCacheMode.Location = new Point(706, 209);
            checkBox_IsCacheMode.Margin = new Padding(5);
            checkBox_IsCacheMode.Name = "checkBox_IsCacheMode";
            checkBox_IsCacheMode.Size = new Size(121, 24);
            checkBox_IsCacheMode.TabIndex = 134;
            checkBox_IsCacheMode.Text = "使用缓存模式";
            checkBox_IsCacheMode.UseVisualStyleBackColor = true;
            // 
            // checkBox_RealIp
            // 
            checkBox_RealIp.AutoSize = true;
            checkBox_RealIp.Location = new Point(706, 242);
            checkBox_RealIp.Name = "checkBox_RealIp";
            checkBox_RealIp.Size = new Size(74, 24);
            checkBox_RealIp.TabIndex = 143;
            checkBox_RealIp.Text = "真实IP";
            checkBox_RealIp.UseVisualStyleBackColor = true;
            // 
            // checkBox_IpDetails
            // 
            checkBox_IpDetails.AutoSize = true;
            checkBox_IpDetails.Location = new Point(706, 275);
            checkBox_IpDetails.Name = "checkBox_IpDetails";
            checkBox_IpDetails.Size = new Size(74, 24);
            checkBox_IpDetails.TabIndex = 144;
            checkBox_IpDetails.Text = "IP详情";
            checkBox_IpDetails.UseVisualStyleBackColor = true;
            // 
            // textBox_ProxyIpUrl
            // 
            textBox_ProxyIpUrl.Location = new Point(130, 46);
            textBox_ProxyIpUrl.Margin = new Padding(5, 4, 5, 4);
            textBox_ProxyIpUrl.Name = "textBox_ProxyIpUrl";
            textBox_ProxyIpUrl.Size = new Size(536, 27);
            textBox_ProxyIpUrl.TabIndex = 118;
            // 
            // linkLabel1
            // 
            linkLabel1.AutoSize = true;
            linkLabel1.Location = new Point(576, 231);
            linkLabel1.Margin = new Padding(4, 0, 4, 0);
            linkLabel1.Name = "linkLabel1";
            linkLabel1.Size = new Size(69, 20);
            linkLabel1.TabIndex = 133;
            linkLabel1.TabStop = true;
            linkLabel1.Text = "开机启动";
            linkLabel1.LinkClicked += linkLabel1_LinkClicked;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(57, 50);
            label1.Margin = new Padding(5, 0, 5, 0);
            label1.Name = "label1";
            label1.Size = new Size(69, 20);
            label1.TabIndex = 117;
            label1.Text = "代理接口";
            // 
            // label100
            // 
            label100.AutoSize = true;
            label100.Location = new Point(57, 17);
            label100.Margin = new Padding(5, 0, 5, 0);
            label100.Name = "label100";
            label100.Size = new Size(69, 20);
            label100.TabIndex = 123;
            label100.Text = "任务接口";
            // 
            // textBox_TaskApiUrl
            // 
            textBox_TaskApiUrl.Location = new Point(130, 13);
            textBox_TaskApiUrl.Margin = new Padding(5, 4, 5, 4);
            textBox_TaskApiUrl.Name = "textBox_TaskApiUrl";
            textBox_TaskApiUrl.Size = new Size(536, 27);
            textBox_TaskApiUrl.TabIndex = 124;
            // 
            // numericUpDown_TaskFetchInterval
            // 
            numericUpDown_TaskFetchInterval.Location = new Point(130, 145);
            numericUpDown_TaskFetchInterval.Margin = new Padding(5, 4, 5, 4);
            numericUpDown_TaskFetchInterval.Maximum = new decimal(new int[] { 60000, 0, 0, 0 });
            numericUpDown_TaskFetchInterval.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_TaskFetchInterval.Name = "numericUpDown_TaskFetchInterval";
            numericUpDown_TaskFetchInterval.Size = new Size(92, 27);
            numericUpDown_TaskFetchInterval.TabIndex = 122;
            numericUpDown_TaskFetchInterval.Value = new decimal(new int[] { 1000, 0, 0, 0 });
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(27, 149);
            label2.Margin = new Padding(5, 0, 5, 0);
            label2.Name = "label2";
            label2.Size = new Size(99, 20);
            label2.TabIndex = 116;
            label2.Text = "任务提取间隔";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(228, 149);
            label3.Margin = new Padding(5, 0, 5, 0);
            label3.Name = "label3";
            label3.Size = new Size(39, 20);
            label3.TabIndex = 114;
            label3.Text = "毫秒";
            // 
            // numericUpDown_MaxConcurrency
            // 
            numericUpDown_MaxConcurrency.Location = new Point(130, 211);
            numericUpDown_MaxConcurrency.Margin = new Padding(5, 4, 5, 4);
            numericUpDown_MaxConcurrency.Maximum = new decimal(new int[] { 1024, 0, 0, 0 });
            numericUpDown_MaxConcurrency.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_MaxConcurrency.Name = "numericUpDown_MaxConcurrency";
            numericUpDown_MaxConcurrency.Size = new Size(92, 27);
            numericUpDown_MaxConcurrency.TabIndex = 121;
            numericUpDown_MaxConcurrency.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // numericUpDown_TaskWorkers
            // 
            numericUpDown_TaskWorkers.Location = new Point(387, 211);
            numericUpDown_TaskWorkers.Maximum = new decimal(new int[] { 1024, 0, 0, 0 });
            numericUpDown_TaskWorkers.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numericUpDown_TaskWorkers.Name = "numericUpDown_TaskWorkers";
            numericUpDown_TaskWorkers.Size = new Size(80, 27);
            numericUpDown_TaskWorkers.TabIndex = 156;
            numericUpDown_TaskWorkers.Value = new decimal(new int[] { 4, 0, 0, 0 });
            // 
            // label_TaskWorkers
            // 
            label_TaskWorkers.AutoSize = true;
            label_TaskWorkers.Location = new Point(269, 214);
            label_TaskWorkers.Name = "label_TaskWorkers";
            label_TaskWorkers.Size = new Size(108, 20);
            label_TaskWorkers.TabIndex = 154;
            label_TaskWorkers.Text = "任务Worker数";
            // 
            // label27
            // 
            label27.AutoSize = true;
            label27.Location = new Point(57, 214);
            label27.Margin = new Padding(5, 0, 5, 0);
            label27.Name = "label27";
            label27.Size = new Size(69, 20);
            label27.TabIndex = 120;
            label27.Text = "并发数量";
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
            panel1.Controls.Add(checkBox_IsCacheMode);
            panel1.Controls.Add(checkBox_RealIp);
            panel1.Controls.Add(checkBox_IpDetails);
            panel1.Controls.Add(label_Status);
            panel1.Controls.Add(linkLabel1);
            panel1.Controls.Add(textBox_ProxyIpUrl);
            panel1.Controls.Add(label_TaskWorkers);
            panel1.Controls.Add(numericUpDown_TaskWorkers);
            panel1.Dock = DockStyle.Top;
            panel1.Location = new Point(0, 0);
            panel1.Name = "panel1";
            panel1.Size = new Size(1190, 327);
            panel1.TabIndex = 142;
            // 
            // linkLabel2
            // 
            linkLabel2.AutoSize = true;
            linkLabel2.Location = new Point(576, 263);
            linkLabel2.Margin = new Padding(5, 0, 5, 0);
            linkLabel2.Name = "linkLabel2";
            linkLabel2.Size = new Size(69, 20);
            linkLabel2.TabIndex = 150;
            linkLabel2.TabStop = true;
            linkLabel2.Text = "应用目录";
            // 
            // label38
            // 
            label38.AutoSize = true;
            label38.Location = new Point(645, 149);
            label38.Margin = new Padding(6, 0, 6, 0);
            label38.Name = "label38";
            label38.Size = new Size(24, 20);
            label38.TabIndex = 149;
            label38.Text = "秒";
            // 
            // label39
            // 
            label39.AutoSize = true;
            label39.Location = new Point(490, 149);
            label39.Margin = new Padding(6, 0, 6, 0);
            label39.Name = "label39";
            label39.Size = new Size(82, 20);
            label39.TabIndex = 147;
            label39.Text = "IP有效时长";
            // 
            // numericUpDown_IpTtl
            // 
            numericUpDown_IpTtl.Location = new Point(576, 145);
            numericUpDown_IpTtl.Margin = new Padding(6, 5, 6, 5);
            numericUpDown_IpTtl.Maximum = new decimal(new int[] { 1800, 0, 0, 0 });
            numericUpDown_IpTtl.Name = "numericUpDown_IpTtl";
            numericUpDown_IpTtl.Size = new Size(66, 27);
            numericUpDown_IpTtl.TabIndex = 148;
            // 
            // label_AcquireRetry
            // 
            label_AcquireRetry.AutoSize = true;
            label_AcquireRetry.Location = new Point(460, 182);
            label_AcquireRetry.Margin = new Padding(6, 0, 6, 0);
            label_AcquireRetry.Name = "label_AcquireRetry";
            label_AcquireRetry.Size = new Size(112, 20);
            label_AcquireRetry.TabIndex = 151;
            label_AcquireRetry.Text = "IP获取重试次数";
            // 
            // numericUpDown_AcquireRetry
            // 
            numericUpDown_AcquireRetry.Location = new Point(576, 178);
            numericUpDown_AcquireRetry.Margin = new Padding(6, 5, 6, 5);
            numericUpDown_AcquireRetry.Maximum = new decimal(new int[] { 10, 0, 0, 0 });
            numericUpDown_AcquireRetry.Name = "numericUpDown_AcquireRetry";
            numericUpDown_AcquireRetry.Size = new Size(66, 27);
            numericUpDown_AcquireRetry.TabIndex = 152;
            // 
            // label_AcquireRetryUnit
            // 
            label_AcquireRetryUnit.AutoSize = true;
            label_AcquireRetryUnit.Location = new Point(645, 182);
            label_AcquireRetryUnit.Margin = new Padding(6, 0, 6, 0);
            label_AcquireRetryUnit.Name = "label_AcquireRetryUnit";
            label_AcquireRetryUnit.Size = new Size(24, 20);
            label_AcquireRetryUnit.TabIndex = 153;
            label_AcquireRetryUnit.Text = "次";
            // 
            // numericUpDown_UvExecutionInterval
            // 
            numericUpDown_UvExecutionInterval.Location = new Point(130, 178);
            numericUpDown_UvExecutionInterval.Margin = new Padding(6, 5, 6, 5);
            numericUpDown_UvExecutionInterval.Maximum = new decimal(new int[] { 60000, 0, 0, 0 });
            numericUpDown_UvExecutionInterval.Name = "numericUpDown_UvExecutionInterval";
            numericUpDown_UvExecutionInterval.Size = new Size(90, 27);
            numericUpDown_UvExecutionInterval.TabIndex = 146;
            numericUpDown_UvExecutionInterval.Value = new decimal(new int[] { 1000, 0, 0, 0 });
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(36, 182);
            label5.Margin = new Padding(6, 0, 6, 0);
            label5.Name = "label5";
            label5.Size = new Size(90, 20);
            label5.TabIndex = 144;
            label5.Text = "UV执行间隔";
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new Point(226, 182);
            label6.Margin = new Padding(6, 0, 6, 0);
            label6.Name = "label6";
            label6.Size = new Size(39, 20);
            label6.TabIndex = 145;
            label6.Text = "毫秒";
            // 
            // textBox_DevApiUrl
            // 
            textBox_DevApiUrl.Location = new Point(130, 79);
            textBox_DevApiUrl.Margin = new Padding(5, 4, 5, 4);
            textBox_DevApiUrl.Name = "textBox_DevApiUrl";
            textBox_DevApiUrl.Size = new Size(536, 27);
            textBox_DevApiUrl.TabIndex = 143;
            textBox_DevApiUrl.Text = "http://117.21.200.18:9000/api/getdev.php";
            // 
            // label14
            // 
            label14.AutoSize = true;
            label14.Location = new Point(57, 82);
            label14.Margin = new Padding(5, 0, 5, 0);
            label14.Name = "label14";
            label14.Size = new Size(69, 20);
            label14.TabIndex = 142;
            label14.Text = "设备接口";
            // 
            // statusStrip1
            // 
            statusStrip1.ImageScalingSize = new Size(20, 20);
            statusStrip1.Items.AddRange(new ToolStripItem[] { toolStripStatusLabel1, toolStripStatusLabel2, toolStripStatusLabel3, toolStripStatusLabel4 });
            statusStrip1.Location = new Point(0, 880);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new Size(1190, 26);
            statusStrip1.TabIndex = 156;
            statusStrip1.Text = "statusStrip1";
            // 
            // toolStripStatusLabel1
            // 
            toolStripStatusLabel1.Name = "toolStripStatusLabel1";
            toolStripStatusLabel1.Size = new Size(167, 20);
            toolStripStatusLabel1.Text = "应用：待启动";
            // 
            // toolStripStatusLabel2
            // 
            toolStripStatusLabel2.Name = "toolStripStatusLabel2";
            toolStripStatusLabel2.Size = new Size(167, 20);
            toolStripStatusLabel2.Text = "CPU：采样中";
            // 
            // toolStripStatusLabel3
            // 
            toolStripStatusLabel3.Name = "toolStripStatusLabel3";
            toolStripStatusLabel3.Size = new Size(167, 20);
            toolStripStatusLabel3.Text = "内存：采样中";
            // 
            // toolStripStatusLabel4
            // 
            toolStripStatusLabel4.Name = "toolStripStatusLabel4";
            toolStripStatusLabel4.Size = new Size(167, 20);
            toolStripStatusLabel4.Text = "浏览器进程：采样中";
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(9F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1190, 906);
            Controls.Add(liveLogView);
            Controls.Add(panel1);
            Controls.Add(statusStrip1);
            MinimumSize = new Size(850, 500);
            Name = "MainForm";
            Text = "MainForm";
            ((System.ComponentModel.ISupportInitialize)numericUpDown_Multiple).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_PageLoadingTimeout).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_TaskFetchInterval).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_MaxConcurrency).EndInit();
            ((System.ComponentModel.ISupportInitialize)numericUpDown_TaskWorkers).EndInit();
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
        private NumericUpDown numericUpDown_TaskWorkers;
        private Label label_TaskWorkers;
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
