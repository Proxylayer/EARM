using System;
using EntraArm.Core;
using Xunit;

namespace EntraArm.Tests;

public sealed class ExpressionSyntaxTests
{
    private const string ComposedFicName = "[format('{0}/{1}', variables('apiName'), format('github-{0}', copyIndex('fics')))]";
    private const string ComposedPasswordName = "[format('{0}/{1}', variables('apiName'), 'ci-secret')]";

    [Theory]
    [InlineData("[x]", true)]
    [InlineData("[]", true)]
    [InlineData("[parameters('a')]", true)]
    [InlineData("[[x]", false)]
    [InlineData("[[", false)]
    [InlineData("x", false)]
    [InlineData("[", false)]
    [InlineData("]", false)]
    [InlineData("[x", false)]
    [InlineData("x]", false)]
    [InlineData(" [x]", false)]
    [InlineData("[x] ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsExpression_RequiresOuterBracketsWithoutTheEscape(string? text, bool expected)
    {
        Assert.Equal(expected, ExpressionSyntax.IsExpression(text));
        if (text is not null)
        {
            Assert.Equal(!expected, ExpressionSyntax.IsLiteral(text));
        }
        else
        {
            Assert.False(ExpressionSyntax.IsLiteral(text));
        }
    }

    [Theory]
    [InlineData("[[x]", true, "[x]")]
    [InlineData("[[", true, "[")]
    [InlineData("[[[x]", true, "[[x]")]
    [InlineData("x", false, "x")]
    [InlineData("[x]", false, "[x]")]
    [InlineData("", false, "")]
    public void IsEscapedLiteral_And_LiteralValue(string text, bool escaped, string literal)
    {
        Assert.Equal(escaped, ExpressionSyntax.IsEscapedLiteral(text));
        Assert.Equal(literal, ExpressionSyntax.LiteralValue(text));
    }

    [Fact]
    public void IsEscapedLiteral_IsFalseForNull()
    {
        Assert.False(ExpressionSyntax.IsEscapedLiteral(null));
        Assert.Throws<ArgumentNullException>(() => ExpressionSyntax.LiteralValue(null!));
    }

    [Fact]
    public void Body_StripsTheOuterBracketsAndRejectsLiterals()
    {
        Assert.Equal("parameters('a')", ExpressionSyntax.Body("[parameters('a')]"));
        Assert.Equal(string.Empty, ExpressionSyntax.Body("[]"));
        Assert.Throws<ArgumentException>(() => ExpressionSyntax.Body("literal"));
        Assert.Throws<ArgumentException>(() => ExpressionSyntax.Body("[[escaped]"));
        Assert.Throws<ArgumentNullException>(() => ExpressionSyntax.Body(null!));
    }

    [Theory]
    [InlineData("a", "'a'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("''", "''''''")]
    [InlineData("", "''")]
    public void QuoteLiteral_DoublesSingleQuotes(string literal, string expected)
    {
        Assert.Equal(expected, ExpressionSyntax.QuoteLiteral(literal));
    }

    [Theory]
    [InlineData("[variables('x')]", "variables('x')")]
    [InlineData("lit", "'lit'")]
    [InlineData("[[lit", "'[lit'")]
    [InlineData("it's", "'it''s'")]
    public void AsArgument_UsesTheBodyOfExpressionsAndQuotesLiterals(string nameOrExpression, string expected)
    {
        Assert.Equal(expected, ExpressionSyntax.AsArgument(nameOrExpression));
    }

    [Theory]
    [InlineData("app", "fic", "app/fic")]
    [InlineData("[[app", "child", "[app/child")]
    [InlineData("[variables('apiName')]", "ci-secret", ComposedPasswordName)]
    [InlineData("[variables('apiName')]", "[format('github-{0}', copyIndex('fics'))]", ComposedFicName)]
    [InlineData("app", "[format('github-{0}', copyIndex('fics'))]", "[format('{0}/{1}', 'app', format('github-{0}', copyIndex('fics')))]")]
    [InlineData("[variables('apiName')]", "it's", "[format('{0}/{1}', variables('apiName'), 'it''s')]")]
    public void ComposeName_JoinsLiteralsAndFormatsExpressions(string parent, string child, string expected)
    {
        Assert.Equal(expected, ExpressionSyntax.ComposeName(parent, child));
    }

    [Theory]
    [InlineData("Microsoft.Entra/applications", "[variables('apiName')]", "[resourceId('Microsoft.Entra/applications', variables('apiName'))]")]
    [InlineData("Microsoft.Entra/applications", "orders-api", "[resourceId('Microsoft.Entra/applications', 'orders-api')]")]
    [InlineData("Microsoft.Entra/applications/federatedIdentityCredentials", "app/fic", "[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', 'app', 'fic')]")]
    [InlineData("Microsoft.Entra/applications/federatedIdentityCredentials", ComposedFicName, "[resourceId('Microsoft.Entra/applications/federatedIdentityCredentials', format('{0}/{1}', variables('apiName'), format('github-{0}', copyIndex('fics'))))]")]
    [InlineData("Microsoft.Entra/users", "it's@contoso.com", "[resourceId('Microsoft.Entra/users', 'it''s@contoso.com')]")]
    [InlineData("Microsoft.Entra/groups", "[[literal", "[resourceId('Microsoft.Entra/groups', '[literal')]")]
    public void ResourceIdExpression_SplitsLiteralSegmentsAndInlinesExpressions(string type, string name, string expected)
    {
        Assert.Equal(expected, ExpressionSyntax.ResourceIdExpression(type, name));
    }

    [Fact]
    public void ComposeName_And_ResourceIdExpression_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => ExpressionSyntax.ComposeName(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => ExpressionSyntax.ComposeName("x", null!));
        Assert.Throws<ArgumentNullException>(() => ExpressionSyntax.ResourceIdExpression(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => ExpressionSyntax.ResourceIdExpression("x", null!));
        Assert.Throws<ArgumentNullException>(() => ExpressionSyntax.QuoteLiteral(null!));
        Assert.Throws<ArgumentNullException>(() => ExpressionSyntax.AsArgument(null!));
    }
}
