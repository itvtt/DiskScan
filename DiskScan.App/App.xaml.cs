using System.Windows;

namespace DiskScan.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 사용 시간 · 검사 · 정리 집계 (AppKit\AppUsage.cs → API 서버, ImPerformance 성과 계산용)
        ILuncher.AppKit.AppUsage.Start();
        // i-luncher AppKit: 설치된 사본이면 시작할 때 GitHub 릴리스를 확인해 스스로 업데이트 (AppKit\AppUpdater.cs).
        // 관리자 권한으로 실행 중이면 업데이터도 같은 권한으로 돌아 설치 폴더(%LOCALAPPDATA%\Programs\DiskScan)를 바꿈
        ILuncher.AppKit.AppUpdater.StartBackground(ready => Dispatcher.BeginInvoke(() =>
        {
            if (MessageBox.Show($"새 버전 {ready.Update.Version}을(를) 받았습니다. (지금 {ready.CurrentVersion})\n\n지금 다시 시작할까요? [아니요]를 누르면 종료할 때 적용됩니다.",
                    "DiskScan 업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                ready.RestartNow();
                Shutdown();
            }
        }));
    }
}
