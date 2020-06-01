﻿using MyPad.Models;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Unity;

namespace MyPad.Views.Regions
{
    /// <summary>
    /// DiffContentView.xaml の相互作用ロジック
    /// </summary>
    public partial class DiffContentView : UserControl
    {
        #region インジェクション

        [Dependency]
        public SettingsService SettingsService { get; set; }

        #endregion

        public DiffContentView()
        {
            InitializeComponent();
        }

        private void UserControl_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && isVisible)
            {
                if (this.SettingsService.System.ShowInlineDiffViewer)
                    this.DiffViewer.ShowInline();
                else
                    this.DiffViewer.ShowSideBySide();
            }
        }
    }
}