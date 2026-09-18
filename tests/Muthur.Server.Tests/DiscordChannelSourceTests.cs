using Muthur.Server.Services;

namespace Muthur.Server.Tests;

/// <summary>
/// Parsing a Discord channel's messages. The rule that matters most is the one that keeps the comms on-call
/// from answering itself: our own replies arrive back in the same channel as bot messages.
/// </summary>
public sealed class DiscordChannelSourceTests
{
    private const string Guilded = "987/1234";
    private const string ChannelOnly = "1234";

    /// <summary>Discord answers newest first; each message here is written in that order.</summary>
    private static string Payload(params string[] messages) => "[" + string.Join(",", messages) + "]";

    private static string FromPerson(string id, string content, string author = "hersh") =>
        $$$"""{"id":"{{{id}}}","content":"{{{content}}}","author":{"username":"{{{author}}}","bot":false}}""";

    private static string FromBot(string id, string content) =>
        $$$"""{"id":"{{{id}}}","content":"{{{content}}}","author":{"username":"MUTHUR","bot":true}}""";

    private static string FromWebhook(string id, string content) =>
        $$$"""{"id":"{{{id}}}","content":"{{{content}}}","webhook_id":"555","author":{"username":"MUTHUR"}}""";

    [Fact]
    public void A_message_someone_typed_becomes_an_inbound_item()
    {
        var fetch = DiscordChannelSource.Parse(Payload(FromPerson("100", "the export is broken")), null, Guilded);

        var item = Assert.Single(fetch.Items);
        Assert.Equal("100", item.ExternalId);
        Assert.Equal("the export is broken", item.Title);
        Assert.Equal("the export is broken", item.Body);
        Assert.Equal("hersh", item.Author);
        Assert.Equal("https://discord.com/channels/987/1234/100", item.Url);
        Assert.Equal("100", fetch.Cursor);
    }

    [Fact]
    public void Our_own_reply_does_not_come_back_in_as_a_new_item()
    {
        // The comms on-call replies through the webhook; the next poll sees that reply in the channel.
        var fetch = DiscordChannelSource.Parse(
            Payload(FromWebhook("102", "Thanks - filed as T-9."), FromBot("101", "posted by a bot"), FromPerson("100", "the export is broken")),
            null, Guilded);

        Assert.Equal("the export is broken", Assert.Single(fetch.Items).Body);
    }

    [Fact]
    public void The_cursor_still_advances_past_messages_that_were_skipped()
    {
        // Otherwise the bot's own reply is fetched again on every poll, forever.
        var fetch = DiscordChannelSource.Parse(Payload(FromWebhook("102", "reply"), FromPerson("100", "question")), null, Guilded);

        Assert.Equal("102", fetch.Cursor);
    }

    [Fact]
    public void Items_come_out_in_the_order_they_were_written()
    {
        var fetch = DiscordChannelSource.Parse(
            Payload(FromPerson("103", "third"), FromPerson("102", "second"), FromPerson("101", "first")), null, Guilded);

        Assert.Equal(["first", "second", "third"], fetch.Items.Select(i => i.Body));
        Assert.Equal("103", fetch.Cursor);
    }

    [Fact]
    public void A_snowflake_is_compared_as_a_number_not_as_text()
    {
        // 19 digits beats 18. Compared as text, "1000000000000000000" sorts below "999999999999999999"
        // and the cursor would go backwards, re-ingesting everything on every poll.
        var fetch = DiscordChannelSource.Parse(
            Payload(FromPerson("1000000000000000000", "newer")), "999999999999999999", Guilded);

        Assert.Equal("1000000000000000000", fetch.Cursor);
    }

    [Fact]
    public void A_cursor_does_not_go_backwards_when_the_page_is_older()
    {
        var fetch = DiscordChannelSource.Parse(Payload(FromPerson("100", "older")), "500", Guilded);

        Assert.Equal("500", fetch.Cursor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_message_with_no_words_in_it_is_skipped(string content)
    {
        // An attachment or a sticker on its own: nothing an on-call can act on.
        var fetch = DiscordChannelSource.Parse(Payload(FromPerson("100", content)), null, Guilded);

        Assert.Empty(fetch.Items);
    }

    [Fact]
    public void The_title_is_the_first_line_and_the_body_keeps_everything()
    {
        var fetch = DiscordChannelSource.Parse(
            Payload(FromPerson("100", "export is broken\\nit 500s on every advisor")), null, Guilded);

        var item = Assert.Single(fetch.Items);
        Assert.Equal("export is broken", item.Title);
        Assert.Equal("export is broken\nit 500s on every advisor", item.Body);
    }

    [Fact]
    public void A_long_first_line_is_cut_short_for_the_title()
    {
        var fetch = DiscordChannelSource.Parse(Payload(FromPerson("100", new string('x', 200))), null, Guilded);

        var item = Assert.Single(fetch.Items);
        Assert.Equal(120, item.Title.Length);
        Assert.EndsWith("…", item.Title);
        Assert.Equal(200, item.Body.Length);
    }

    [Fact]
    public void Without_a_guild_there_is_no_link_back_to_the_message()
    {
        var fetch = DiscordChannelSource.Parse(Payload(FromPerson("100", "hello")), null, ChannelOnly);

        Assert.Null(Assert.Single(fetch.Items).Url);
    }

    [Theory]
    [InlineData("987/1234", "987", "1234")]
    [InlineData("1234", null, "1234")]
    public void A_location_is_a_channel_or_a_guild_and_a_channel(string location, string? guild, string channel) =>
        Assert.Equal((guild, channel), DiscordChannelSource.SplitLocation(location));
}
