// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

// blueprint.api uses the per-class factory variant: BlueprintAppFactory is an IClassFixture of each HTTP
// test class rather than an assembly fixture, because its tests arrange and assert on the NSubstitute
// doubles of the four sibling API clients and the xAPI service the host resolves. See BlueprintAppFactory.

using Blueprint.Api.Tests.Support;

// Starting a PostgreSQL container and running the migrations costs seconds, so it happens once for the
// whole assembly. xUnit v3 constructs this before the first test and injects it into any test class (or
// class fixture) with a matching constructor parameter. The container itself starts on the first test that
// asks for a database (see PostgresTestDatabase), so tests that need none run without Docker.
[assembly: AssemblyFixture(typeof(DatabaseFixture))]
