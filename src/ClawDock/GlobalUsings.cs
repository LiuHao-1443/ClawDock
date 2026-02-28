// 消除 WPF 和 WinForms 同时启用时的命名冲突
global using Application = System.Windows.Application;
global using MessageBox   = System.Windows.MessageBox;
global using Color        = System.Windows.Media.Color;
global using Brushes      = System.Windows.Media.Brushes;
