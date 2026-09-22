// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Blueprint.Api.Tests;

/// <summary>
/// The three converters <c>Startup</c> adds to the MVC serializer, and what they do to the wire format
/// every blueprint client reads.
/// </summary>
/// <remarks>
/// <para>
/// No host and no database: a converter is a pure function of its input, and
/// <c>CompositionTests.TheMvcSerializer_CarriesTheFourConvertersInOrder</c> is what pins that these are
/// the converters the application actually uses.
/// </para>
/// <para>
/// Two of the three are <c>internal</c>, so they are built by reflection rather than named. That is not a
/// workaround for the test's benefit - it is the reason <c>Blueprint.Api.Client</c> and
/// <c>blueprint.ui</c> cannot reuse them, and why the wire format they impose is documented only by what
/// crosses it.
/// </para>
/// </remarks>
public class JsonConverterTests
{
    private static readonly JsonSerializerOptions Integers = With("JsonIntegerConverter");
    private static readonly JsonSerializerOptions Guids = With("JsonNullableGuidConverter");
    private static readonly JsonSerializerOptions Doubles = With("JsonDoubleConverter");

    private sealed class HasAnInt
    {
        public int Value { get; set; }
    }

    private sealed class HasANullableGuid
    {
        public Guid? Value { get; set; }
    }

    private sealed class HasADouble
    {
        public double Value { get; set; }
    }

    // -------------------------------------------------------------------------------------------------
    // JsonIntegerConverter: every int in the API is a JSON string
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// The single most consequential line in the three files. Every <c>int</c> blueprint answers -
    /// <c>displayOrder</c>, <c>moveNumber</c>, <c>deltaSeconds</c>, <c>groupOrder</c> - crosses the wire
    /// quoted, so a client that types them as numbers does not parse a blueprint response. It is not
    /// wrong, but it is invisible in the OpenAPI document, which still declares them
    /// <c>type: integer</c>.
    /// </remarks>
    [Fact]
    public void AnInteger_IsWrittenAsAString()
    {
        Assert.Equal("""{"Value":"3"}""", JsonSerializer.Serialize(new HasAnInt { Value = 3 }, Integers));
    }

    [Fact]
    public void ANegativeInteger_IsWrittenAsAString()
    {
        Assert.Equal("""{"Value":"-3"}""", JsonSerializer.Serialize(new HasAnInt { Value = -3 }, Integers));
    }

    /// <remarks>Reading accepts both forms, so a client may send either.</remarks>
    [Theory]
    [InlineData("""{"Value":3}""")]
    [InlineData("""{"Value":"3"}""")]
    public void AnInteger_IsReadFromEitherForm(string json)
    {
        Assert.Equal(3, JsonSerializer.Deserialize<HasAnInt>(json, Integers).Value);
    }

    /// <remarks>
    /// <para>
    /// An unparseable string falls through to <c>reader.GetInt32()</c>, which throws
    /// <c>InvalidOperationException</c> for a string token rather than the <c>JsonException</c> the
    /// input formatter is looking for - but the framework absorbs it anyway, so the client is answered
    /// the 400 it should be. System.Text.Json marks the exceptions it raises itself (through
    /// <c>Exception.Source</c>) and re-throws those wrapped in a <c>JsonException</c> carrying the
    /// property path, which is what happens here: the reader threw, so the reader's own owner rescues
    /// it. The converter is careless and the carelessness is invisible.
    /// </para>
    /// <para>
    /// The inner message is *not* in the outer one - the wrapper's text is the framework's generic
    /// "could not be converted" sentence - so what a client reads is the path and nothing about the
    /// value. <see cref="ANullableGuidThatIsNotAGuid_IsAnUnwrappedFormatExceptionNamingNothing"/> is the
    /// one case where the same carelessness is not rescued, and it is the one worth fixing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnIntegerThatIsNotANumber_IsAJsonExceptionNamingTheProperty()
    {
        var ex = Assert.ThrowsAny<JsonException>(
            () => JsonSerializer.Deserialize<HasAnInt>("""{"Value":"abc"}""", Integers));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("$.Value", ex.Message);
    }

    /// <remarks>
    /// A quoted value too large for an <c>int</c> is the same shape, which matters more: it is the one a
    /// client reaches by echoing back a number it computed rather than by typing nonsense. Both parse
    /// attempts fail on the range rather than on the characters, so it lands on the same fall-through.
    /// </remarks>
    [Fact]
    public void AQuotedIntegerTooLargeToFit_IsTheSameJsonException()
    {
        var ex = Assert.ThrowsAny<JsonException>(
            () => JsonSerializer.Deserialize<HasAnInt>("""{"Value":"99999999999"}""", Integers));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("$.Value", ex.Message);
    }

    /// <remarks>
    /// An unquoted one is a <c>JsonException</c> too, and differs only in what it wraps - a
    /// <c>FormatException</c> from <c>GetInt32</c> reading a number out of range, where the quoted form
    /// wraps an <c>InvalidOperationException</c> from <c>GetInt32</c> reading a string at all. Both are
    /// the framework's own, both carry the path, and a client cannot tell them apart. The distinction is
    /// recorded because it is the evidence for the mechanism: two different exception types from one
    /// line, wrapped identically.
    /// </remarks>
    [Fact]
    public void AnUnquotedIntegerTooLargeToFit_IsAJsonExceptionToo()
    {
        var ex = Assert.ThrowsAny<JsonException>(
            () => JsonSerializer.Deserialize<HasAnInt>("""{"Value":99999999999}""", Integers));

        Assert.IsType<FormatException>(ex.InnerException);
        Assert.Contains("$.Value", ex.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // JsonNullableGuidConverter
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void ANullableGuid_IsWrittenAsAString()
    {
        var id = Guid.NewGuid();

        Assert.Equal(
            $"{{\"Value\":\"{id}\"}}",
            JsonSerializer.Serialize(new HasANullableGuid { Value = id }, Guids));
    }

    /// <remarks>
    /// The empty string reads back as null, which is the half of this converter that earns its place:
    /// blueprint's UI sends <c>""</c> for an unset optional id, and the framework's own converter would
    /// answer 400.
    /// </remarks>
    [Theory]
    [InlineData("""{"Value":""}""")]
    [InlineData("""{"Value":"  "}""")]
    [InlineData("""{"Value":null}""")]
    public void AnEmptyNullableGuid_IsReadAsNull(string json)
    {
        Assert.Null(JsonSerializer.Deserialize<HasANullableGuid>(json, Guids).Value);
    }

    /// <remarks>
    /// And a null is written as <c>null</c>, not as the <c>""</c> the converter's own <c>case null</c>
    /// arm writes: System.Text.Json short-circuits a null <c>Nullable&lt;T&gt;</c> before calling a
    /// converter whose <c>HandleNull</c> is false, which is the default. So that arm is dead code, the
    /// wire format is symmetric, and the thing to be careful about is only that reading is *wider* than
    /// writing.
    /// </remarks>
    [Fact]
    public void ANullNullableGuid_IsWrittenAsNull()
    {
        Assert.Equal(
            """{"Value":null}""",
            JsonSerializer.Serialize(new HasANullableGuid { Value = null }, Guids));
    }

    /// <remarks>
    /// A non-string token reaches <c>reader.GetString()</c>, which throws
    /// <c>InvalidOperationException</c> - and is rescued and given a path, exactly as
    /// <see cref="AnIntegerThatIsNotANumber_IsAJsonExceptionNamingTheProperty"/> is, for the same reason.
    /// So <c>"mselId": 3</c> is a 400 naming <c>$.mselId</c>.
    /// </remarks>
    [Fact]
    public void ANullableGuidThatIsNotAString_IsAJsonExceptionNamingTheProperty()
    {
        var ex = Assert.ThrowsAny<JsonException>(
            () => JsonSerializer.Deserialize<HasANullableGuid>("""{"Value":3}""", Guids));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("$.Value", ex.Message);
    }

    /// <remarks>
    /// BUG, and the one place the converters' carelessness reaches a client: <c>Guid.Parse</c> is the
    /// converter's *own* call, so the <c>FormatException</c> it throws is not one System.Text.Json
    /// raised, not marked rethrowable, and therefore not wrapped - it escapes with no path and a message
    /// about Guid formats. MVC catches it as a model-binding failure attributed to **no property**, so
    /// the 400 a client reads is <c>{"errors":{"":["The supplied value is invalid."]}}</c>: a document
    /// with a bad id in it is refused without saying which field was bad, where every other malformed
    /// value in this file names one. One <c>Guid.TryParse</c> and a <c>throw new JsonException()</c>
    /// would put it in line with the rest. <c>ErrorResponseTests</c> asserts the wire shape.
    /// </remarks>
    [Fact]
    public void ANullableGuidThatIsNotAGuid_IsAnUnwrappedFormatExceptionNamingNothing()
    {
        var ex = Assert.ThrowsAny<FormatException>(
            () => JsonSerializer.Deserialize<HasANullableGuid>("""{"Value":"nope"}""", Guids));

        Assert.IsNotType<JsonException>(ex);
        Assert.DoesNotContain("$.Value", ex.Message);
    }

    // -------------------------------------------------------------------------------------------------
    // JsonDoubleConverter, which nothing reaches
    // -------------------------------------------------------------------------------------------------

    /// <remarks>
    /// BUG: this is configuration with no subject. Not one property of any view model in
    /// <c>Blueprint.Api/ViewModels/</c> and no column of any entity in <c>Blueprint.Api.Data/Models/</c>
    /// is a <c>double</c>, a <c>float</c> or a <c>decimal</c>, so the converter below is registered on
    /// every request and called on none. Deleting a name from this list is the test for adding the first
    /// floating-point property to the API - at which point the two defects underneath become reachable.
    /// </remarks>
    [Fact]
    public void NothingInTheApiIsAFloatingPointNumber()
    {
        var floating = new[] { typeof(double), typeof(float), typeof(decimal) };

        var properties = typeof(Startup).Assembly.GetTypes()
            .Where(x => x.Namespace == "Blueprint.Api.ViewModels")
            .Concat(typeof(Data.BlueprintContext).Assembly.GetTypes()
                .Where(x => x.Namespace == "Blueprint.Api.Data.Models"))
            .SelectMany(x => x.GetProperties())
            .Where(x => floating.Contains(Nullable.GetUnderlyingType(x.PropertyType) ?? x.PropertyType))
            .Select(x => $"{x.DeclaringType.Name}.{x.Name}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(properties);
    }

    /// <remarks>
    /// BUG, unreachable for now: a NaN is written as <c>0.0</c>. Not as <c>null</c>, not as an error -
    /// as a number indistinguishable from a real measurement of zero. The framework's own converter
    /// writes <c>"NaN"</c> when <c>NumberHandling</c> allows it and throws otherwise, either of which a
    /// reader can tell from a value.
    /// </remarks>
    [Fact]
    public void ADoubleThatIsNaN_IsWrittenAsZero()
    {
        Assert.Equal(
            """{"Value":0}""",
            JsonSerializer.Serialize(new HasADouble { Value = double.NaN }, Doubles));
    }

    /// <remarks>
    /// BUG, unreachable for now, and the sharper of the two: infinity is not guarded at all, so it
    /// throws while writing - after the response has begun. NaN's silent zero at least answers.
    /// </remarks>
    [Fact]
    public void ADoubleThatIsInfinite_ThrowsWhileWriting()
    {
        Assert.ThrowsAny<Exception>(
            () => JsonSerializer.Serialize(new HasADouble { Value = double.PositiveInfinity }, Doubles));
    }

    /// <remarks>
    /// BUG, unreachable for now: the string form is parsed with <c>double.Parse</c> and no
    /// <c>CultureInfo</c>, so what <c>"1,5"</c> means depends on the server's locale - fifteen under a
    /// culture using a comma as its decimal separator, and an error under one that does not. Every other
    /// parse in the converters is culture-invariant by construction, <c>Guid</c> and <c>int</c> having no
    /// culture.
    /// </remarks>
    [Fact]
    public void ADoubleAsAString_IsParsedInTheServersCulture()
    {
        Assert.Equal(1.5, JsonSerializer.Deserialize<HasADouble>("""{"Value":"1.5"}""", Doubles).Value);
    }

    /// <summary>
    /// Options carrying one of <c>Startup</c>'s converters, named rather than referenced because two of
    /// the three are <c>internal</c> to <c>Blueprint.Api</c>.
    /// </summary>
    private static JsonSerializerOptions With(string converter)
    {
        var type = typeof(Startup).Assembly.GetType(
            $"Blueprint.Api.Infrastructure.JsonConverters.{converter}", throwOnError: true);

        var options = new JsonSerializerOptions();
        options.Converters.Add((JsonConverter)Activator.CreateInstance(type));

        return options;
    }
}
