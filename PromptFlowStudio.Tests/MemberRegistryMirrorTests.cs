using System;
using System.IO;
using PromptFlowStudio.Services.Account;

namespace PromptFlowStudio.Tests;

/// <summary>회원 원장 미러 — 로컬 유실 시 자동 복원 · 가입/비번변경 시 원자적 미러 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class MemberRegistryMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pfs_mirror_" + Guid.NewGuid().ToString("N"));
    private string Local => Path.Combine(_root, "local", "members.json");
    private string MirrorDir => Path.Combine(_root, "mirror");
    private string MirrorFile => Path.Combine(MirrorDir, "members.json");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* 정리 실패 무시 */ } }

    private static MemberRegisterRequest Req(string id, string pw) =>
        new(id, pw, "테스터", "2000-01-01", "M", "t@example.com", "010-0000-0000", "", "", "", "", "", "", "", "");

    [Fact]
    public void Register_MirrorsLedger_AndRestoresAfterLocalWipe()
    {
        var a = new MemberRegistryService(Local, MirrorDir);
        Assert.True(a.RegisterFull(Req("ADMINX", "pass1234")).Ok);
        Assert.True(File.Exists(MirrorFile));
        Assert.False(File.Exists(MirrorFile + ".tmp"));

        File.Delete(Local);   // C: 정리로 원장 유실 시뮬
        var b = new MemberRegistryService(Local, MirrorDir);
        Assert.True(b.RestoredFromMirror);
        Assert.True(File.Exists(Local));
        Assert.True(b.Authenticate("ADMINX", "pass1234").Ok);
    }

    [Fact]
    public void PasswordChange_UpdatesMirror()
    {
        var a = new MemberRegistryService(Local, MirrorDir);
        Assert.True(a.RegisterFull(Req("ADMINX", "pass1234")).Ok);
        Assert.True(a.SetPasswordForId("ADMINX", "newpass99"));

        File.Delete(Local);
        var b = new MemberRegistryService(Local, MirrorDir);
        Assert.True(b.Authenticate("ADMINX", "newpass99").Ok);
        Assert.False(b.Authenticate("ADMINX", "pass1234").Ok);
    }

    [Fact]
    public void ExistingLocal_IsNeverOverwrittenByMirror()
    {
        var a = new MemberRegistryService(Local, MirrorDir);
        Assert.True(a.RegisterFull(Req("ADMINX", "pass1234")).Ok);
        File.WriteAllText(MirrorFile, "[]");

        var b = new MemberRegistryService(Local, MirrorDir);
        Assert.False(b.RestoredFromMirror);
        Assert.True(b.Exists("ADMINX"));
    }

    [Fact]
    public void CorruptOrEmptyMirror_IsNotRestored()
    {
        Directory.CreateDirectory(MirrorDir);
        File.WriteAllText(MirrorFile, "{not json");
        var b = new MemberRegistryService(Local, MirrorDir);
        Assert.False(b.RestoredFromMirror);
        Assert.False(File.Exists(Local));
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void NullMirror_DisablesSync()
    {
        var a = new MemberRegistryService(Local, null);
        Assert.True(a.RegisterFull(Req("ADMINX", "pass1234")).Ok);
        Assert.False(Directory.Exists(MirrorDir));
    }
}
