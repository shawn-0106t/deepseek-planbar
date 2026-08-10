// 解决 UseWindowsForms 引入的命名冲突：全项目统一以 WPF 类型为准
global using Application = System.Windows.Application;
global using Timer = System.Threading.Timer;
global using RadioButton = System.Windows.Controls.RadioButton;
global using CheckBox = System.Windows.Controls.CheckBox;
// WPF 的隐式 using 集合不含 System.IO，显式补上
global using System.IO;
