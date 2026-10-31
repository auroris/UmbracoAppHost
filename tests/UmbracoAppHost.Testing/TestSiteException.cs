namespace UmbracoAppHost.Testing;

/// <summary>A test site did not behave as expected; the message says what was observed.</summary>
public sealed class TestSiteException(string message, Exception? inner = null) : Exception(message, inner);
