using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SoundManager.Core;

namespace SoundManager.Windows;

public partial class MainWindow
{
    private void OpenEqualizer(object sender,RoutedEventArgs e)
    {
        var settings=mixer.Settings;var body=new StackPanel{Margin=new Thickness(24)};
        var window=new Window{Title="이퀄라이저",Content=body,Owner=this,Width=430,SizeToContent=SizeToContent.Height,
            ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background,Foreground=Foreground,FontFamily=FontFamily,Icon=Icon};
        body.Children.Add(new TextBlock{Text="이퀄라이저",FontSize=21,FontWeight=FontWeights.Bold,Margin=new(0,0,0,16)});
        Slider Band(string name,float initial)
        {
            var label=new TextBlock();var slider=new Slider{Minimum=-12,Maximum=12,Value=initial,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new(0,8,0,16)};
            void Update()=>label.Text=$"{name}: {slider.Value:F0} dB";
            slider.ValueChanged+=(_,_)=>Update();Update();body.Children.Add(label);body.Children.Add(slider);return slider;
        }
        var bass=Band("저음",settings.BassDb);var treble=Band("고음",settings.TrebleDb);var presence=Band("명료도",settings.PresenceDb);
        var presets=new WrapPanel();body.Children.Add(presets);
        foreach(var (name,label) in new[]{("flat","기본"),("bass","저음 강조"),("treble","고음 강조"),("soft","부드럽게"),("clear","선명하게")})
        {
            var button=new Button{Content=label,Padding=new(10,7,10,7),Margin=new(0,0,8,8)};
            button.Click+=(_,_)=>{var value=MixSettings.Preset(name);bass.Value=value.BassDb;treble.Value=value.TrebleDb;presence.Value=value.PresenceDb;};presets.Children.Add(button);
        }
        var apply=new Button{Content="적용",Padding=new(20,9,20,9),HorizontalAlignment=HorizontalAlignment.Right,IsDefault=true};body.Children.Add(apply);
        apply.Click+=(_,_)=>{mixer.Settings=mixer.Settings with{BassDb=(float)bass.Value,TrebleDb=(float)treble.Value,PresenceDb=(float)presence.Value};window.Close();};
        window.ShowDialog();
    }
    private void OpenProcessing(object sender,RoutedEventArgs e)
    {
        if(server is not {} backend){Status.Text="먼저 서버를 시작하세요.";return;}
        var settings=backend.Snapshot().Processing??new ProcessingSettings();
        var body=new StackPanel{Margin=new Thickness(24)};
        var window=new Window{Title="음량 자동 보정 · 출력 보호",Content=body,Owner=this,Width=430,SizeToContent=SizeToContent.Height,
            ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background,Foreground=Foreground,FontFamily=FontFamily,Icon=Icon};
        body.Children.Add(new TextBlock{Text="음량 자동 보정 · 출력 보호",FontSize=21,FontWeight=FontWeights.Bold,Margin=new(0,0,0,16)});
        var limiter=new CheckBox{Content="합산 출력의 과도한 피크 줄이기",IsChecked=settings.LimiterEnabled,Foreground=Brushes.White,Margin=new(0,8,0,8)};
        var clock=new CheckBox{Content="기기별 재생 속도 차이 보정",IsChecked=settings.ClockCorrection,Foreground=Brushes.White,Margin=new(0,8,0,8)};
        body.Children.Add(limiter);body.Children.Add(clock);
        Slider Level(string name,float value,double min,double max,string unit)
        {
            var label=new TextBlock();var slider=new Slider{Minimum=min,Maximum=max,Value=value,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new(0,8,0,18)};
            void Changed()=>label.Text=$"{name}: {slider.Value:F0} {unit}";
            slider.ValueChanged+=(_,_)=>Changed();Changed();body.Children.Add(label);body.Children.Add(slider);return slider;
        }
        var target=Level("자동 보정 기준",settings.TargetDbfs,-36,-6,"dBFS");
        var maximum=Level("최대 증폭",settings.MaxGainDb,0,24,"dB");
        body.Children.Add(new TextBlock{Text="기기 메뉴 → 음량 조절에서 자동 보정을 켤 수 있습니다. dBFS는 디지털 신호 기준이며 실제 이어폰·스피커 음압을 뜻하지 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,14)});
        var result=new TextBlock{TextWrapping=TextWrapping.Wrap};body.Children.Add(result);
        var apply=new Button{Content="적용",Padding=new(20,9,20,9),HorizontalAlignment=HorizontalAlignment.Right,IsDefault=true};body.Children.Add(apply);
        apply.Click+=async(_,_)=>
        {
            apply.IsEnabled=false;
            try
            {
                if(server!=backend)throw new InvalidOperationException();
                await backend.SetProcessingAsync(settings with{LimiterEnabled=limiter.IsChecked==true,ClockCorrection=clock.IsChecked==true,TargetDbfs=(float)target.Value,MaxGainDb=(float)maximum.Value});
                window.Close();
            }
            catch(Exception){result.Text="저장하지 못했습니다. 서버 연결을 확인한 뒤 다시 적용하세요.";}
            finally{apply.IsEnabled=true;}
        };
        window.ShowDialog();
    }
}
