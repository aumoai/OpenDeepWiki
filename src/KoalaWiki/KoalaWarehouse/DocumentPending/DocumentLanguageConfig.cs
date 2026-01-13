namespace KoalaWiki.KoalaWarehouse.DocumentPending;

/// <summary>
/// 文档生成语言配置
/// </summary>
public static class DocumentLanguageConfig
{
    private static string? _defaultLanguage;

    /// <summary>
    /// 获取文档生成语言设置
    /// </summary>
    public static string GetLanguageSetting()
    {
        // 优先级: 缓存值 > DOC_LANGUAGE环境变量 > LANGUAGE环境变量 > 默认值
        if (_defaultLanguage != null)
            return _defaultLanguage;

        var envLanguage = Environment.GetEnvironmentVariable("DOC_LANGUAGE")?.Trim();

        if (string.IsNullOrEmpty(envLanguage))
        {
            // 如果DOC_LANGUAGE未设置，尝试使用LANGUAGE环境变量作为回退
            envLanguage = Environment.GetEnvironmentVariable("LANGUAGE")?.Trim();
        }

        if (!string.IsNullOrEmpty(envLanguage))
        {
            _defaultLanguage = envLanguage;
            return _defaultLanguage;
        }

        // 默认使用中文
        _defaultLanguage = "zh-CN";
        return _defaultLanguage;
    }

    /// <summary>
    /// 获取语言提示词片段
    /// </summary>
    public static string GetLanguageReminder()
    {
        var language = GetLanguageSetting();

        return language.ToLower() switch
        {
            "zh-cn" or "chinese" or "中文" =>
                """
                ## Language Requirement
                **CRITICAL**: Generate the ENTIRE document in Simplified Chinese (简体中文).
                - All titles, headings, paragraphs, lists, and explanatory text MUST be in Chinese
                - Code identifiers, file paths, API names remain in their original form
                - Citations (file_path:line_number) remain as-is
                - Technical terms can include English in parentheses, e.g., "依赖注入 (Dependency Injection)"
                """,

            "en" or "english" =>
                """
                ## Language Requirement
                **CRITICAL**: Generate the ENTIRE document in English.
                - All titles, headings, paragraphs, lists, and explanatory text MUST be in English
                - Code identifiers, file paths, API names remain in their original form
                - Citations (file_path:line_number) remain as-is
                """,

            "ja" or "japanese" or "日本語" =>
                """
                ## Language Requirement
                **CRITICAL**: Generate the ENTIRE document in Japanese (日本語).
                - All titles, headings, paragraphs, lists, and explanatory text MUST be in Japanese
                - Code identifiers, file paths, API names remain in their original form
                - Citations (file_path:line_number) remain as-is
                """,

            _ =>
                $"""
                ## Language Requirement
                **CRITICAL**: Generate the ENTIRE document in {language}.
                - All titles, headings, paragraphs, lists, and explanatory text MUST be in {language}
                - Code identifiers, file paths, API names remain in their original form
                - Citations (file_path:line_number) remain as-is
                """
        };
    }

    /// <summary>
    /// 设置语言（用于测试或运行时覆盖）
    /// </summary>
    public static void SetLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Language cannot be empty", nameof(language));

        _defaultLanguage = language.Trim();
    }

    /// <summary>
    /// 重置为默认值（用于测试）
    /// </summary>
    public static void Reset()
    {
        _defaultLanguage = null;
    }
}
