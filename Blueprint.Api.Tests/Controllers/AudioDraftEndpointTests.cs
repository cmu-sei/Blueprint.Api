// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using Blueprint.Api.Data.Models;
using Blueprint.Api.Infrastructure.Options;
using Blueprint.Api.Services;
using Blueprint.Api.Tests.Support;
using Blueprint.Api.ViewModels;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace Blueprint.Api.Tests.Controllers;

/// <summary>
/// <c>POST msels/{mselId}/ai-draft/audio</c> and <c>GET msels/{mselId}/ai-draft/audio/available</c> - an inject voiced: its
/// article read aloud, or a voicemail, call, broadcast or news reel the model writes. The speech provider, the text model and the
/// image model are substitutes. Nothing is saved.
/// </summary>
public class AudioDraftEndpointTests(DatabaseFixture fixture, AiEnabledFactory factory)
    : ApiTestBase(fixture, factory), IClassFixture<AiEnabledFactory>
{
    private static readonly List<AiVoice> Voices =
    [
        new("Matthew", "Male", "US English", true),
        new("Joanna", "Female", "US English", true),
        new("Gregory", "Male", "US English"),
        new("Ruth", "Female", "US English")
    ];

    private readonly IAiModelClient _model = factory.Model;
    private readonly IAiSpeechClient _speech = factory.Speech;
    private readonly IAiImageClient _images = factory.Images;
    private readonly List<(string Text, string Voice, string Style)> _spoken = new();
    private string _system;
    private string _prompt;
    private string _schema;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _model.ClearSubstitute();
        _speech.ClearSubstitute();
        _images.ClearSubstitute();
        _speech.IsConfigured.Returns(true);
        _speech.Voices.Returns(Voices);
        // each line is 1.2 seconds, its sentence at the start and its words 300 ms apart
        _speech.SpeakAsync(default, default, default, default).ReturnsForAnyArgs(call =>
        {
            var (text, voice, style) = (call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2));
            lock (_spoken)
                _spoken.Add((text, voice, style));
            return new AiSpeech
            {
                Mp3 = Mp3Frames.Lasting(1200),
                DurationMs = 1200,
                Sentences = [new AiSpeechMark { Time = 0, Text = text }],
                Words = text.Split(' ').Select((w, i) => new AiSpeechMark { Time = i * 300, Text = w }).ToList()
            };
        });
    }

    [Fact]
    public async Task Available_WithASpeechProvider_SaysWhatCanBeMade()
    {
        var (msel, _) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var available = await Read<AudioDraftAvailability>(await Client(editor).GetAsync($"/api/msels/{msel.Id}/ai-draft/audio/available", Ct));

        Assert.Equal((true, true, false), (available.Available, available.Scripts, available.Photos));
    }

    [Fact]
    public async Task Available_WithoutASpeechProvider_IsNothing()
    {
        _speech.IsConfigured.Returns(false);
        _images.IsConfigured.Returns(true);
        var (msel, _) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var available = await Read<AudioDraftAvailability>(await Client(editor).GetAsync($"/api/msels/{msel.Id}/ai-draft/audio/available", Ct));

        Assert.Equal((false, false, false), (available.Available, available.Scripts, available.Photos));
    }

    [Fact]
    public async Task Available_ToAViewer_Is403()
    {
        var (msel, _) = await SeedMsel();
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Client(viewer).GetAsync($"/api/msels/{msel.Id}/ai-draft/audio/available", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Available_OfAMselThatDoesNotExist_Is404()
    {
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Client(actor).GetAsync($"/api/msels/{Guid.NewGuid()}/ai-draft/audio/available", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Listen reads the headline and the article as written, in the outlet's own voice, with no model call; the marking and the
    /// old transcript are not read, and the clip is the voiced line with its timings.
    /// </summary>
    [Fact]
    public async Task Listen_ReadsTheArticleInTheOutletsVoiceWithoutTheModel()
    {
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var article = ArticleMarkings.Apply("<p>Two stations went offline at six.</p><details><summary>Transcript</summary><p>Old.</p></details>", null, null, msel.Name);

        var result = await Read<AudioDraftResult>(await Post(editor, msel.Id, Request(f, AudioDraftKind.Listen, article)));

        Assert.Empty(_model.ReceivedCalls());
        var voice = AudioDraftService.PickVoice(Voices, "WPN News").Name;
        Assert.Equal([("Pumps stop. Two stations went offline at six.", voice, null)], _spoken);
        var line = Assert.Single(result.Lines);
        Assert.Equal(("WPN News", voice), (line.Speaker, line.Voice));
        Assert.StartsWith("data:audio/mpeg;base64,", line.Audio);
        Assert.Equal(1200, line.DurationMs);
        Assert.Equal(8, line.Words.Count);
        Assert.Equal((AudioDraftKind.Listen, false, 2, "Pumps stop"), (result.Kind, result.Phone, result.Seconds, result.Headline));
        Assert.Null(result.Transcript);
        Assert.Equal("EXERCISE · EXERCISE · EXERCISE", result.Marking.Text);
        Assert.False(await ReadBack(rb => rb.DataValues.AnyAsync(v => v.Value.Contains("audio/mpeg"), Ct)));
    }

    [Fact]
    public async Task Listen_OfAnEmptyArticle_Is400()
    {
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var request = Request(f, AudioDraftKind.Listen, "<p> </p>");
        request.Values.Remove(f.Title.Id);

        var response = await Post(editor, msel.Id, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_spoken);
    }

    /// <summary>An article longer than a clip may be is refused before anything is voiced, and says how to change the limit.</summary>
    [Fact]
    public async Task Listen_OfAnArticleTooLongToVoice_Is400()
    {
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Post(editor, msel.Id, Request(f, AudioDraftKind.Listen, "<p>" + string.Join(" ", Enumerable.Repeat("The pumps stopped.", 400)) + "</p>"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Ai:Speech:MaxCharacters", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(_spoken);
    }

    [Fact]
    public async Task Draft_WithoutASpeechProvider_Is400SayingWhatToSet()
    {
        _speech.IsConfigured.Returns(false);
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Post(editor, msel.Id, Request(f, AudioDraftKind.Voicemail));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Set Ai:Speech on the Blueprint API", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(_model.ReceivedCalls());
    }

    [Fact]
    public async Task Draft_ByAViewer_Is403WithoutVoicingAnything()
    {
        var (msel, f) = await SeedMsel();
        var viewer = await Actor().OnMsel(msel, MselRole.Viewer).SeedAsync();

        var response = await Post(viewer, msel.Id, Request(f, AudioDraftKind.Listen));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_spoken);
    }

    [Fact]
    public async Task Draft_WithEditMsels_IsAllowedWithoutARole()
    {
        var (msel, f) = await SeedMsel();
        var actor = await Actor().WithSystemPermissions(SystemPermission.EditMsels).SeedAsync();

        var response = await Post(actor, msel.Id, Request(f, AudioDraftKind.Listen));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A field of another MSEL cannot be used to read that MSEL into a script.</summary>
    [Fact]
    public async Task Draft_OfAnotherMselsField_Is404()
    {
        var (msel, f) = await SeedMsel();
        var (_, other) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var response = await Post(editor, msel.Id, Request(other, AudioDraftKind.Listen));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_spoken);
    }

    /// <summary>Only an HTML field can hold audio or video.</summary>
    [Fact]
    public async Task Draft_IntoATextField_Is400()
    {
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var request = Request(f, AudioDraftKind.Listen);
        request.DataFieldId = f.Title.Id;

        var response = await Post(editor, msel.Id, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is not an HTML field", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>A voicemail is written by the model from the inject, sounds as if over a phone, and has its transcript.</summary>
    [Fact]
    public async Task Voicemail_IsWrittenThenVoiced()
    {
        Answer("""{"lines":[{"speaker":"Dana Ruiz","voice":"Joanna","text":"Hi, it's Dana at the pipeline. Call me back."}],"notes":["Assumed Dana works nights."]}""");
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<AudioDraftResult>(await Post(editor, msel.Id, Request(f, AudioDraftKind.Voicemail, instruction: "She sounds tired")));

        Assert.Contains("a voicemail the inject's sender leaves", _system);
        Assert.Contains("Pumps stop", _prompt);
        Assert.Contains("Two stations went offline", _prompt);
        Assert.Contains("- Gregory: Male, US English", _prompt);
        Assert.Contains("She sounds tired", _prompt);
        Assert.Contains("\"enum\":[\"Matthew\",\"Joanna\",\"Gregory\",\"Ruth\"]", _schema);
        Assert.Equal([("Hi, it's Dana at the pipeline. Call me back.", "Joanna", null)], _spoken);
        Assert.Equal((true, "Voicemail", 2), (result.Phone, result.Title, result.Seconds));
        Assert.Equal("<details><summary><b>Transcript</b></summary><p><b>DANA RUIZ:</b> Hi, it&#39;s Dana at the pipeline. Call me back.</p></details>", result.Transcript);
        Assert.Equal(["Assumed Dana works nights."], result.Notes);
    }

    /// <summary>An intercepted call has each speaker in their own voice, voiced line by line in order.</summary>
    [Fact]
    public async Task Call_VoicesEachSpeakerInTheirOwnVoiceInOrder()
    {
        Answer("""
            {"lines":[{"speaker":"CALLER 1","voice":"Gregory","text":"Are you seeing it?"},
                      {"speaker":"CALLER 2","voice":"Gregory","text":"I see it."},
                      {"speaker":"CALLER 1","voice":"Gregory","text":"Keep it quiet."}]}
            """);
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<AudioDraftResult>(await Post(editor, msel.Id, Request(f, AudioDraftKind.Call)));

        Assert.Contains("two or more speakers", _system);
        Assert.Equal([("CALLER 1", "Gregory"), ("CALLER 2", "Matthew"), ("CALLER 1", "Gregory")], result.Lines.Select(l => (l.Speaker, l.Voice)));
        Assert.Equal((true, "Intercept", 4), (result.Phone, result.Title, result.Seconds));
        Assert.Contains("<p><b>CALLER 2:</b> I see it.</p>", result.Transcript);
    }

    /// <summary>A broadcast's anchor is the outlet's voice, reading as a newscaster where the voice can; a quoted official is not.</summary>
    [Fact]
    public async Task Broadcast_HasTheOutletsAnchorInTheNewsStyle()
    {
        Answer("""
            {"lines":[{"speaker":"Anchor","voice":"Ruth","text":"Good evening."},
                      {"speaker":"Official","voice":"Ruth","text":"No one is in danger."}]}
            """);
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();
        var anchor = AudioDraftService.PickVoice(Voices, "WPN News");

        var result = await Read<AudioDraftResult>(await Post(editor, msel.Id, Request(f, AudioDraftKind.Broadcast)));

        Assert.Contains($"The anchor is the outlet's own voice, {anchor.Name}", _prompt);
        Assert.Equal(anchor.Name, result.Lines[0].Voice);
        Assert.NotEqual(anchor.Name, result.Lines[1].Voice);
        Assert.Contains(_spoken, s => s.Text == "Good evening." && s.Style == (anchor.News ? AiSpeechStyle.News : null));
        Assert.Contains(_spoken, s => s.Text == "No one is in danger." && s.Style == null);
        Assert.Equal((false, "WPN News", "WPN News"), (result.Phone, result.Title, result.Outlet));
    }

    /// <summary>A reel uses the article's own photo, so no photo is made, and comes back with its post, handle and word timings.</summary>
    [Fact]
    public async Task Reel_UsesTheArticlesPhoto()
    {
        _images.IsConfigured.Returns(true);
        AnswerReel();
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<AudioDraftResult>(await Post(editor, msel.Id,
            Request(f, AudioDraftKind.Reel, "<img src=\"data:image/jpeg;base64,UEhPVE8=\" alt=\"Pump\"><p>Two stations went offline at six.</p>")));

        Assert.Contains("vertical social media news reel", _system);
        Assert.Equal(("article", "data:image/jpeg;base64,UEhPVE8="), (result.PhotoSource, result.Photo));
        await _images.DidNotReceive().PhotoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(("WPN", "@WPNNews", "Two stations offline. #WPN", "WPN"), (result.Outlet, result.Handle, result.Post, result.Title));
        var line = Assert.Single(result.Lines);
        Assert.Equal(AudioDraftService.PickVoice(Voices, "WPN News").Name, line.Voice);
        Assert.Equal([0, 300, 600, 900], line.Words.Select(w => w.StartMs));
        Assert.NotNull(result.Transcript);
    }

    /// <summary>Without a photo of its own, a reel gets one of the scene the model describes, where an image model is configured.</summary>
    [Fact]
    public async Task Reel_WithoutAPhoto_MakesOne()
    {
        _images.IsConfigured.Returns(true);
        _images.PhotoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("data:image/jpeg;base64,TkVX");
        AnswerReel();
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<AudioDraftResult>(await Post(editor, msel.Id, Request(f, AudioDraftKind.Reel)));

        await _images.Received(1).PhotoAsync("a pumping station at dawn", Arg.Any<CancellationToken>());
        Assert.Equal(("generated", "data:image/jpeg;base64,TkVX"), (result.PhotoSource, result.Photo));
    }

    /// <summary>Without a photo or an image model, the reel is drawn on a plain card.</summary>
    [Fact]
    public async Task Reel_WithoutAPhotoOrImageModel_IsAPlainCard()
    {
        AnswerReel();
        var (msel, f) = await SeedMsel();
        var editor = await Actor().OnMsel(msel, MselRole.Editor).SeedAsync();

        var result = await Read<AudioDraftResult>(await Post(editor, msel.Id, Request(f, AudioDraftKind.Reel)));

        Assert.Equal("none", result.PhotoSource);
        Assert.Null(result.Photo);
    }

    // ---------------------------------------------------------------------------------------------

    private void AnswerReel() =>
        Answer("""{"script":"Two stations are down.","post":"Two stations offline. #WPN","outlet":"WPN","handle":"@WPNNews","scene":"a pumping station at dawn"}""");

    private void Answer(string json) =>
        _model.InvokeToolAsync(default, default, default, default, default, default)
            .ReturnsForAnyArgs(callInfo =>
            {
                _system = callInfo.ArgAt<string>(0);
                _prompt = callInfo.ArgAt<string>(1);
                _schema = callInfo.ArgAt<System.Text.Json.Nodes.JsonObject>(4)?.ToJsonString();
                return JsonDocument.Parse(json).RootElement.Clone();
            });

    private async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, Ct);
    }

    private Task<HttpResponseMessage> Post(TestActor actor, Guid mselId, AudioDraftRequest request) =>
        Client(actor).PostAsJsonAsync($"/api/msels/{mselId}/ai-draft/audio", request, JsonOptions, Ct);

    private static AudioDraftRequest Request(Fields f, AudioDraftKind kind, string body = "<p>Two stations went offline at six.</p>", string instruction = null) =>
        new()
        {
            DataFieldId = f.Body.Id,
            Kind = kind,
            Instruction = instruction,
            Values = new Dictionary<Guid, string> { [f.Title.Id] = "Pumps stop", [f.Source.Id] = "WPN News", [f.Body.Id] = body }
        };

    private record Fields(DataFieldEntity Title, DataFieldEntity Source, DataFieldEntity Body);

    /// <summary>A Gallery MSEL with a title, a source name and an HTML body</summary>
    private async Task<(MselEntity, Fields)> SeedMsel()
    {
        var msel = TestData.Msel();
        msel.UseGallery = true;
        msel.Description = "A cyber intrusion stops a pipeline.";
        var title = TestData.DataField(msel.Id, name: "Title", displayOrder: 1);
        title.GalleryArticleParameter = "Name";
        var source = TestData.DataField(msel.Id, name: "Source", displayOrder: 2);
        source.GalleryArticleParameter = "SourceName";
        var body = TestData.DataField(msel.Id, dataType: DataFieldType.Html, name: "Description", displayOrder: 3);
        body.GalleryArticleParameter = "Description";
        await Seed(msel, title, source, body);
        return (msel, new Fields(title, source, body));
    }
}
