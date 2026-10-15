// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Blueprint.Api.Tests.Infrastructure.JsonConverters;

/// <summary>The three converters <c>Startup</c> adds to the MVC serializer, and what they do to the wire
/// format every blueprint client reads.</summary>
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

    /// <summary>Every <c>int</c> is written as a JSON string.</summary>
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

    /// <summary>An integer that is not a number is a <c>JsonException</c> naming the property.</summary>
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

    /// <summary>An unquoted integer too large for an <c>int</c> is a <c>JsonException</c> naming the
    /// property.</summary>
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

    /// <summary>A null <c>Guid?</c> is written as <c>null</c>.</summary>
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

    /// <summary>A nullable guid that is not a guid is an unwrapped format exception naming nothing.</summary>
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

    /// <summary>Nothing in the API is a floating point number.</summary>
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

    /// <summary>A NaN is written as <c>0.0</c>.</summary>
    [Fact]
    public void ADoubleThatIsNaN_IsWrittenAsZero()
    {
        Assert.Equal(
            """{"Value":0}""",
            JsonSerializer.Serialize(new HasADouble { Value = double.NaN }, Doubles));
    }

    /// <summary>An infinite double throws while writing.</summary>
    [Fact]
    public void ADoubleThatIsInfinite_ThrowsWhileWriting()
    {
        Assert.ThrowsAny<Exception>(
            () => JsonSerializer.Serialize(new HasADouble { Value = double.PositiveInfinity }, Doubles));
    }

    /// <summary>A double in a string is parsed in the server's culture.</summary>
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
