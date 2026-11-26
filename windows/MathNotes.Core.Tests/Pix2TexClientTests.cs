using MathNotes.Core;
using Xunit;

namespace MathNotes.Core.Tests;

public class Pix2TexClientTests
{
    [Fact]
    public void ParseOutput_Erfolg()
    {
        var r = Pix2TexClient.ParseOutput(
            "{\"engine\":\"pix2tex\",\"engine_version\":\"0.1.4\",\"candidates\":[{\"latex\":\"\\\\frac{a}{b}\",\"confidence\":null}]}\n");
        Assert.True(r.Recognized);
        Assert.Equal("\\frac{a}{b}", r.Candidates[0].Latex);
        Assert.Null(r.Candidates[0].Confidence);
        Assert.Equal("0.1.4", r.EngineVersion);
    }

    [Fact]
    public void ParseOutput_MitKonfidenz()
    {
        var r = Pix2TexClient.ParseOutput("{\"engine\":\"x\",\"engine_version\":\"1\",\"candidates\":[{\"latex\":\"a\",\"confidence\":0.75}]}");
        Assert.Equal(0.75, r.Candidates[0].Confidence);
    }

    [Fact]
    public void ParseOutput_Fehlerzeile()
    {
        var r = Pix2TexClient.ParseOutput("{\"error\":\"Komponente fehlt: pix2tex\"}");
        Assert.False(r.Recognized);
        Assert.Contains("fehlt", r.Error);
    }

    [Fact]
    public void ParseOutput_StoerausgabeDavor_WirdIgnoriert()
    {
        var r = Pix2TexClient.ParseOutput("Warnung irgendwas\n{\"engine\":\"pix2tex\",\"engine_version\":\"1\",\"candidates\":[]}\n");
        Assert.False(r.Recognized);
        Assert.Null(r.Error);
    }

    [Fact]
    public void ParseOutput_Muell_GibtFehlerStattAbsturz()
    {
        Assert.NotNull(Pix2TexClient.ParseOutput("kein json").Error);
        Assert.NotNull(Pix2TexClient.ParseOutput("{kaputt").Error);
    }

    [Fact]
    public async Task Recognize_OhneInstallation_GibtFehlerStattAbsturz()
    {
        var c = new Pix2TexClient(@"C:\\gibt\\es\\nicht\\python.exe", "x.py");
        Assert.False(c.IsInstalled);
        var r = await c.RecognizeAsync(new[] { new StrokeData("#000", 2, new[] { new PointData(0, 0, .5, 0) }) }, TimeSpan.FromSeconds(5));
        Assert.False(r.Recognized);
        Assert.NotNull(r.Error);
    }
}
