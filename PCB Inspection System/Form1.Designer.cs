namespace PCB_Inspection_System
{
    partial class Form1
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
            btn_detect = new Button();
            pictureBox1 = new PictureBox();
            btn_load = new Button();
            lblOK = new Label();
            lblNG = new Label();
            pictureBox2 = new PictureBox();
            cb_choose_Model = new ComboBox();
            lb_Status = new Label();
            label1 = new Label();
            ((System.ComponentModel.ISupportInitialize)pictureBox1).BeginInit();
            ((System.ComponentModel.ISupportInitialize)pictureBox2).BeginInit();
            SuspendLayout();
            // 
            // btn_detect
            // 
            btn_detect.Location = new Point(1150, 12);
            btn_detect.Name = "btn_detect";
            btn_detect.Size = new Size(75, 23);
            btn_detect.TabIndex = 0;
            btn_detect.Text = "Detect";
            btn_detect.UseVisualStyleBackColor = true;
            btn_detect.Click += btn_detect_Click;
            // 
            // pictureBox1
            // 
            pictureBox1.Location = new Point(6, 12);
            pictureBox1.Name = "pictureBox1";
            pictureBox1.Size = new Size(741, 711);
            pictureBox1.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBox1.TabIndex = 1;
            pictureBox1.TabStop = false;
            // 
            // btn_load
            // 
            btn_load.Location = new Point(1249, 12);
            btn_load.Name = "btn_load";
            btn_load.Size = new Size(75, 23);
            btn_load.TabIndex = 2;
            btn_load.Text = "Load";
            btn_load.UseVisualStyleBackColor = true;
            btn_load.Click += btn_load_Click;
            // 
            // lblOK
            // 
            lblOK.AutoSize = true;
            lblOK.Location = new Point(1231, 112);
            lblOK.Name = "lblOK";
            lblOK.Size = new Size(38, 15);
            lblOK.TabIndex = 3;
            lblOK.Text = "label1";
            // 
            // lblNG
            // 
            lblNG.AutoSize = true;
            lblNG.Location = new Point(1187, 112);
            lblNG.Name = "lblNG";
            lblNG.Size = new Size(38, 15);
            lblNG.TabIndex = 3;
            lblNG.Text = "label1";
            // 
            // pictureBox2
            // 
            pictureBox2.Location = new Point(753, 246);
            pictureBox2.Name = "pictureBox2";
            pictureBox2.Size = new Size(585, 477);
            pictureBox2.SizeMode = PictureBoxSizeMode.Zoom;
            pictureBox2.TabIndex = 4;
            pictureBox2.TabStop = false;
            // 
            // cb_choose_Model
            // 
            cb_choose_Model.FormattingEnabled = true;
            cb_choose_Model.Location = new Point(1172, 58);
            cb_choose_Model.Name = "cb_choose_Model";
            cb_choose_Model.Size = new Size(121, 23);
            cb_choose_Model.TabIndex = 5;
            cb_choose_Model.SelectedIndexChanged += cb_choose_Model_SelectedIndexChanged;
            // 
            // lb_Status
            // 
            lb_Status.AutoSize = true;
            lb_Status.Location = new Point(1187, 228);
            lb_Status.Name = "lb_Status";
            lb_Status.Size = new Size(35, 15);
            lb_Status.TabIndex = 6;
            lb_Status.Text = "Done";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(1128, 228);
            label1.Name = "label1";
            label1.Size = new Size(39, 15);
            label1.TabIndex = 6;
            label1.Text = "Status";
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1350, 735);
            Controls.Add(label1);
            Controls.Add(lb_Status);
            Controls.Add(cb_choose_Model);
            Controls.Add(pictureBox2);
            Controls.Add(lblNG);
            Controls.Add(lblOK);
            Controls.Add(btn_load);
            Controls.Add(pictureBox1);
            Controls.Add(btn_detect);
            Name = "Form1";
            Text = "PCB_Inspection";
            FormClosing += Form1_FormClosing;
            Load += Form1_Load;
            ((System.ComponentModel.ISupportInitialize)pictureBox1).EndInit();
            ((System.ComponentModel.ISupportInitialize)pictureBox2).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button btn_detect;
        private PictureBox pictureBox1;
        private Button btn_load;
        private Label lblOK;
        private Label lblNG;
        private PictureBox pictureBox2;
        private ComboBox cb_choose_Model;
        private Label lb_Status;
        private Label label1;
    }
}
