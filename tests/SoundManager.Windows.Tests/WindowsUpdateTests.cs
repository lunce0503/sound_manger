using System.IO;
using SoundManager.Core;
using SoundManager.Windows;

namespace SoundManager.Windows.Tests;

public sealed class WindowsUpdateTests
{
    [Fact] public async Task PreferencesAndFailedVersionSurviveRestartWithoutTouchingPairing()
    {
        string root=Path.Combine(Path.GetTempPath(),"SoundManager-update-preferences-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string pairing=Path.Combine(root,"connection.dpapi");File.WriteAllBytes(pairing,[1,2,3]);
            await using(var first=new WindowsUpdates(new Version(0,4,1),_=>{},root))
            {Assert.True(first.Preferences.Automatic);first.SetPreferences(false,false);first.MarkFailed("0.4.2");}
            await using(var second=new WindowsUpdates(new Version(0,4,1),_=>{},root))
            {Assert.False(second.Preferences.Automatic);Assert.False(second.Preferences.ApplyWhenIdle);Assert.Equal("0.4.2",second.Preferences.FailedVersion);Assert.False(second.MayApplyAutomatically);}
            Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(pairing));
        }
        finally {Directory.Delete(root,true);}
    }
    [Fact] public void ResumePreservesMixRoleVisibilityAndRejectsAnotherHandoff()
    {
        string root=Path.Combine(Path.GetTempPath(),"SoundManager-update-resume-"+Guid.NewGuid().ToString("N"));
        try
        {
            var resume=new UpdateResume(Guid.NewGuid().ToString("N"),"0.4.2",true,false,false,true,new MixSettings(.38f,4,-2,1));
            string path=Path.Combine(root,"updates","resume.json");WindowsUpdates.Save(path,resume);
            Assert.Equal(resume,WindowsUpdates.Resume(resume.Token,root));
            Assert.Null(WindowsUpdates.Resume(Guid.NewGuid().ToString("N"),root));
            WindowsUpdates.Save(path,resume with{Sender=true});Assert.Null(WindowsUpdates.Resume(resume.Token,root));
            WindowsUpdates.Save(path,resume with{Mix=new MixSettings(2)});Assert.Null(WindowsUpdates.Resume(resume.Token,root));
        }
        finally {Directory.Delete(root,true);}
    }
}
