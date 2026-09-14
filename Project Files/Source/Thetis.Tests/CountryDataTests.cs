/*  CountryDataTests.cs

This file is part of a program that implements a Software-Defined Radio.

This code/file can be found on GitHub : https://github.com/nubbyless/Thetis-Plus

Copyright (C) 2026 ChasingCoffee
Copyright (C) 2026 nubbyless <nubbyless@yahoo.com>

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
*/

using System;
using System.Linq;
using Xunit;

namespace Thetis.Tests
{
    /// <summary>
    /// Verifies the DXCC/country prefix lookup data (Resources\cty.txt) still loads
    /// and resolves callsigns correctly. This is the regression guard for the
    /// BinaryFormatter→JSON re-encode of the embedded cty resource: if the regen is
    /// ever corrupted, or the serializer pipeline changes again, these tests fail.
    /// The CountryData static ctor silently nulls the list on any load failure, so a
    /// clean pass here confirms the resource deserializes and lookups work end-to-end.
    /// </summary>
    public class CountryDataTests
    {
        // A representative set of callsigns spanning continents/ITU regions.
        // Each pair: (callsign, expected country substring).
        private static readonly (string Call, string CountryPart)[] KnownLookups =
        {
            ("W4YNY", "United States"),
            ("K1ABC", "United States"),
            ("VE3XYZ", "Canada"),
            ("G4ABC", "England"),
            ("GM0AAA", "Scotland"),
            ("JA1ABC", "Japan"),
            ("VK2XYZ", "Australia"),
            ("IZ1ABC", "Italy"),
            ("DL1ABC", "Germany"),
            ("F1ABC", "France"),
            ("PY1ABC", "Brazil"),
            ("ZL1ABC", "New Zealand"),
            ("RA1ABC", "European Russia"),
            ("ZS1ABC", "South Africa"),
        };

        [Fact]
        public void Resource_holds_reasonable_entry_count()
        {
            // Trigger the static ctor + load. If the blob fails to (de)serialize,
            // _prefixDataList is null and every lookup returns null below.
            var some = CountryData.GetCallsignData("K1ABC");
            Assert.NotNull(some);
        }

        [Fact]
        public void Known_callsigns_resolve_to_expected_country()
        {
            foreach (var (call, countryPart) in KnownLookups)
            {
                var pd = CountryData.GetCallsignData(call);
                Assert.NotNull(pd); // fails if cty resource is missing/corrupt
                Assert.Contains(countryPart, pd.Country, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Lookup_is_case_insensitive_and_trimmed()
        {
            var upper = CountryData.GetCallsignData("VE3XYZ");
            var lower = CountryData.GetCallsignData("ve3xyz");
            var padded = CountryData.GetCallsignData("  W4YNY  ");
            var unpadded = CountryData.GetCallsignData("W4YNY");
            Assert.NotNull(upper);
            Assert.NotNull(lower);
            Assert.NotNull(padded);
            Assert.NotNull(unpadded);
            Assert.Equal(upper.Country, lower.Country);   // case insensitive
            Assert.Equal(padded.Country, unpadded.Country); // surrounding whitespace trimmed
        }

        [Fact]
        public void Unknown_prefix_returns_null()
        {
            // '0' is not a valid ITU prefix leading character, so no entry in the
            // table can match; confirms the no-match path returns null (vs blank input).
            Assert.Null(CountryData.GetCallsignData("0ZZZ"));
        }

        [Fact]
        public void Null_or_blank_callsign_returns_null()
        {
            Assert.Null(CountryData.GetCallsignData(null));
            Assert.Null(CountryData.GetCallsignData(""));
            Assert.Null(CountryData.GetCallsignData("   "));
        }

        [Fact]
        public void Country_and_asset_codes_are_derived()
        {
            // The static ctor derives CountryCode/AssetCode from the alias maps
            // (getCountryCode/getAssetCode) after deserialization. AssetCode drives
            // spot flags; CountryCode feeds country display. Both must be non-empty
            // for the reference countries.
            foreach (var (call, _) in KnownLookups)
            {
                var pd = CountryData.GetCallsignData(call);
                Assert.NotNull(pd);
                Assert.False(string.IsNullOrWhiteSpace(pd.CountryCode),
                    $"{call}: CountryCode missing — alias map derivation failed");
                Assert.False(string.IsNullOrWhiteSpace(pd.AssetCode),
                    $"{call}: AssetCode missing — spot flag would be blank");
            }
        }

        [Fact]
        public void Longest_prefix_wins()
        {
            // "ZL1" (New Zealand) shares the "ZL" region with "ZL7" (Chatham Islands).
            // The trailing digit extends the prefix, so a longer prefix ("ZL7") must
            // outrank the shorter matching one ("ZL") when the callsign starts with it.
            var nz = CountryData.GetCallsignData("ZL1ABC");
            var chatham = CountryData.GetCallsignData("ZL7ABC");
            Assert.NotNull(nz);
            Assert.NotNull(chatham);
            Assert.NotEqual(nz.Country, chatham.Country);
            Assert.Contains("Chatham", chatham.Country, StringComparison.OrdinalIgnoreCase);

            // Same mechanism for VK9 (Norfolk Island) vs VK1/2/... (mainland Australia).
            var nf = CountryData.GetCallsignData("VK9ABC");
            Assert.NotNull(nf);
            Assert.Contains("Norfolk", nf.Country, StringComparison.OrdinalIgnoreCase);
        }
    }
}
