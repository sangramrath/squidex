﻿// ==========================================================================
//  DateTimeFieldTests.cs
//  Squidex Headless CMS
// ==========================================================================
//  Copyright (c) Squidex Group
//  All rights reserved.
// ==========================================================================

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using NodaTime;
using Xunit;

namespace Squidex.Core.Schemas
{
    public class DateTimeFieldTests
    {
        private readonly List<string> errors = new List<string>();

        [Fact]
        public void Should_instantiate_field()
        {
            var sut = new DateTimeField(1, "my-datetime", new DateTimeFieldProperties());

            Assert.Equal("my-datetime", sut.Name);
        }

        [Fact]
        public void Should_clone_object()
        {
            var sut = new DateTimeField(1, "my-datetime", new DateTimeFieldProperties());

            Assert.NotEqual(sut, sut.Enable());
        }

        [Fact]
        public async Task Should_not_add_error_if_valid()
        {
            var sut = new DateTimeField(1, "my-datetime", new DateTimeFieldProperties { Label = "My-DateTime" });

            await sut.ValidateAsync(CreateValue(null), errors);

            Assert.Empty(errors);
        }

        [Fact]
        public async Task Should_add_errors_if_datetime_is_required()
        {
            var sut = new DateTimeField(1, "my-datetime", new DateTimeFieldProperties { Label = "My-DateTime", IsRequired = true });

            await sut.ValidateAsync(CreateValue(null), errors);

            errors.ShouldBeEquivalentTo(
                new[] { "My-DateTime is required" });
        }

        [Fact]
        public async Task Should_add_errors_if_datetime_is_less_than_min()
        {
            var sut = new DateTimeField(1, "my-datetime", new DateTimeFieldProperties { Label = "My-DateTime", MinValue = FutureDays(10) });

            await sut.ValidateAsync(CreateValue(FutureDays(0)), errors);

            errors.ShouldBeEquivalentTo(
                new[] { $"My-DateTime must be greater than '{FutureDays(10)}'" });
        }

        [Fact]
        public async Task Should_add_errors_if_datetime_is_greater_than_max()
        {
            var sut = new DateTimeField(1, "my-datetime", new DateTimeFieldProperties { Label = "My-DateTime", MaxValue = FutureDays(10) });

            await sut.ValidateAsync(CreateValue(FutureDays(20)), errors);

            errors.ShouldBeEquivalentTo(
                new[] { $"My-DateTime must be less than '{FutureDays(10)}'" });
        }

        [Fact]
        public async Task Should_add_errors_if_value_is_not_valid()
        {
            var sut = new DateTimeField(1, "my-datetime", new DateTimeFieldProperties { Label = "My-DateTime" });

            await sut.ValidateAsync(CreateValue("Invalid"), errors);

            errors.ShouldBeEquivalentTo(
                new[] { "My-DateTime is not a valid value" });
        }

        private static Instant FutureDays(int days)
        {
            return SystemClock.Instance.GetCurrentInstant().Plus(Duration.FromDays(days));
        }

        private static JValue CreateValue(object v)
        {
            return v is Instant ? new JValue(v.ToString()) : new JValue(v);
        }
    }
}
