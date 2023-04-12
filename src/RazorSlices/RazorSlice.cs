﻿using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Internal;
using Microsoft.AspNetCore.Razor.TagHelpers;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;

namespace RazorSlices;

/// <summary>
/// Base class for a Razor Slice template.
/// </summary>
public abstract partial class RazorSlice : IDisposable
{
    private HtmlEncoder _htmlEncoder = HtmlEncoder.Default;
    private IBufferWriter<byte>? _bufferWriter;
    private TextWriter? _textWriter;
    private Utf8BufferTextWriter? _utf8BufferTextWriter;
    private Func<CancellationToken, ValueTask>? _outputFlush;
    private Dictionary<string, Func<Task>>? _sectionWriters;

    /// <summary>
    /// 
    /// </summary>
    public string? Layout { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public RazorSlice? PreviousSlice { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public HtmlString? PreviousBodyContent { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public IReadOnlyDictionary<string, Func<Task>>? SectionWriters
    {
        get
        {
            return _sectionWriters?.AsReadOnly();
        }
        set 
        {
            _sectionWriters = (Dictionary<string, Func<Task>>?)value;
        }
    }

    /// <summary>
    /// Implemented by the generated template class.
    /// </summary>
    /// <remarks>
    /// This method should not be called directly. Call
    /// <see cref="RenderAsync(IBufferWriter{byte}, Func{CancellationToken, ValueTask}?, HtmlEncoder?, IServiceProvider?)"/> or
    /// <see cref="RenderAsync(TextWriter, HtmlEncoder?)"/> instead to render the template.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the execution of the template.</returns>
    public abstract Task ExecuteAsync();

    /// <summary>
    /// Renders the template to the specified <see cref="IBufferWriter{T}"/>.
    /// </summary>
    /// <param name="bufferWriter">The <see cref="IBufferWriter{T}"/> to render the template to.</param>
    /// <param name="flushAsync">An optional delegate that flushes the <see cref="IBufferWriter{T}"/>.</param>
    /// <param name="serviceProvider"></param>
    /// <param name="htmlEncoder">An optional <see cref="HtmlEncoder"/> instance to use when rendering the template. If none is specified, <see cref="HtmlEncoder.Default"/> will be used.</param>
    /// <returns>A <see cref="ValueTask"/> representing the rendering of the template.</returns>
    [MemberNotNull(nameof(_bufferWriter))]
    public ValueTask RenderAsync(IBufferWriter<byte> bufferWriter, Func<CancellationToken, ValueTask>? flushAsync = null, HtmlEncoder? htmlEncoder = null, IServiceProvider? serviceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(bufferWriter);

        // TODO: Render via layout if LayoutAttribute is set

        // if current slice has layout: create and render layout, and pass in this ExecuteAsync to delegate body rendering.
        if (!string.IsNullOrEmpty(Layout))
        {
            return RenderLayout(bufferWriter, flushAsync, htmlEncoder, serviceProvider);
        }

        _bufferWriter = bufferWriter;
        _textWriter = null;
        _outputFlush = flushAsync;
        _htmlEncoder = htmlEncoder ?? _htmlEncoder;

        // Make the previous sectionWriter write using the BodyWriter.
        if (PreviousSlice != null)
        {
            PreviousSlice._bufferWriter = _bufferWriter;
        }

        var executeTask = ExecuteAsync();

        if (executeTask.IsCompletedSuccessfully)
        {
            executeTask.GetAwaiter().GetResult();
            return ValueTask.CompletedTask;
        }
        return new ValueTask(executeTask);

        // TODO: Should we explicitly flush here if flushAsync is not null?
    }

    /// <summary>
    /// Renders the template to the specified <see cref="TextWriter"/>.
    /// </summary>
    /// <param name="textWriter">The <see cref="TextWriter"/> to render the template to.</param>
    /// <param name="htmlEncoder">An optional <see cref="HtmlEncoder"/> instance to use when rendering the template. If none is specified, <see cref="HtmlEncoder.Default"/> will be used.</param>
    /// <returns>A <see cref="ValueTask"/> representing the rendering of the template.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="textWriter"/> is <c>null</c>.</exception>
    [MemberNotNull(nameof(_textWriter), nameof(_outputFlush))]
    public ValueTask RenderAsync(TextWriter textWriter, HtmlEncoder? htmlEncoder = null)
    {
        ArgumentNullException.ThrowIfNull(textWriter);

        // TODO: Render via layout if LayoutAttribute is set

        _bufferWriter = null;
        _textWriter = textWriter;
        _outputFlush = (_) =>
        {
            var flushTask = textWriter.FlushAsync();
            if (flushTask.IsCompletedSuccessfully)
            {
                return ValueTask.CompletedTask;
            }
            return AwaitOutputFlushTask(flushTask);
        };
        _htmlEncoder = htmlEncoder ?? _htmlEncoder;

        var executeTask = ExecuteAsync();

        if (executeTask.IsCompletedSuccessfully)
        {
            return ValueTask.CompletedTask;
        }
        return new ValueTask(executeTask);
    }

    private ValueTask RenderLayout(IBufferWriter<byte> bufferWriter, Func<CancellationToken, ValueTask>? flushAsync = null, HtmlEncoder? htmlEncoder = null, IServiceProvider? serviceProvider = null)
    {
        // Run current slice ExecuteAsync and store the HtmlContent (generated by ExecuteAsync) to PreviousBodyContent (but don't write with bufferWriter yet),
        // ExecuteAsync will populate the _sectionWriters by calling DefineSection(name, required)
        // and write the content that is not specified in any section (body content).
        // Then temporarily store the content as HtmlString writen by using the _textWriter.
        if (Layout == null)
        {
            throw new InvalidOperationException("RenderLayout is being called while Layout is null");
        }

        _bufferWriter = null;
        _textWriter ??= new StringWriter();
        _outputFlush = flushAsync;
        _htmlEncoder = htmlEncoder ?? _htmlEncoder;

        // To capture the previous section writer output
        // Make the previous section writer use the current slice _textWriter.
        if (PreviousSlice != null)
        {
            PreviousSlice._textWriter!.FlushAsync().GetAwaiter().GetResult();
            PreviousSlice._textWriter.Close();
            PreviousSlice._textWriter = _textWriter;
        }

        var executeCurrentTask = ExecuteAsync();

        //_bufferWriter = bufferWriter;

        if (executeCurrentTask.IsCompletedSuccessfully)
        {
            executeCurrentTask.GetAwaiter().GetResult();
        }

        // Create SliceFactory or SliceWithServicesFactory if Slice has injectable properties.
        SliceDefinition layoutDefinition = ResolveSliceDefinition(Layout);
        RazorSlice? layoutSlice;
        if (layoutDefinition.HasInjectableProperties)
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException($"Layout {layoutDefinition.Identifier} has injectable properties but IServiceProvider is not provided");
            }
            SliceWithServicesFactory factory = (SliceWithServicesFactory)layoutDefinition.Factory;
            layoutSlice = factory(serviceProvider!);
        }
        else
        {
            SliceFactory factory = (SliceFactory)layoutDefinition.Factory;
            layoutSlice = factory();
        }

        // Set sections to _sectionsWriters defined in current slice ExecuteAsync
        layoutSlice.SectionWriters = _sectionWriters;

        // Capture output written by current slice ExecuteAsync,
        // The HtmlString will then rendered via RenderBody() method
        layoutSlice.PreviousBodyContent = new HtmlString(_textWriter.ToString());
        layoutSlice.PreviousSlice = this;

        // Render layout slice
        var layoutTask = layoutSlice.RenderAsync(bufferWriter, flushAsync, htmlEncoder: htmlEncoder, serviceProvider: serviceProvider);
        if (layoutTask.IsCompletedSuccessfully)
        {
            layoutTask.GetAwaiter().GetResult();
            return ValueTask.CompletedTask;
        }
        return layoutTask;
    }

    /// <summary>
    /// Renders the portion of a content page that is not within a named section.
    /// </summary>
    /// <returns>The HTML content to render.</returns>
    protected virtual IHtmlContent RenderBody()
    {
        if (PreviousBodyContent == null)
        {
            return HtmlString.Empty;
        }
        WriteHtml(PreviousBodyContent);
        return PreviousBodyContent;
    }

    private static async ValueTask AwaitOutputFlushTask(Task flushTask)
    {
        await flushTask;
    }

    /// <summary>
    /// Indicates whether <see cref="FlushAsync(CancellationToken)"/> will actually flush the underlying output during rendering.
    /// </summary>
    protected bool CanFlush => _outputFlush is not null;

    /// <summary>
    /// Attempts to flush the underlying output the template is being rendered to. Check <see cref="CanFlush"/> to determine if
    /// the output will actually be flushed or not before calling this method.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>A <see cref="ValueTask"/> representing the flush operation.</returns>
    protected ValueTask<HtmlString> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!CanFlush || _outputFlush is null)
        {
            return ValueTask.FromResult(HtmlString.Empty);
        }

        var flushTask = _outputFlush(cancellationToken);

        if (flushTask.IsCompletedSuccessfully)
        {
            flushTask.GetAwaiter().GetResult();
            return ValueTask.FromResult(HtmlString.Empty);
        }

        return AwaitFlushAsyncTask(flushTask);
    }

    private static async ValueTask<HtmlString> AwaitFlushAsyncTask(ValueTask flushTask)
    {
        await flushTask;
        return HtmlString.Empty;
    }

    /// <summary>
    /// Defines a section that can be rendered on-demand via <see cref="RenderSectionAsync(string, bool)"/>.
    /// </summary>
    /// <remarks>
    /// You generally shouldn't call this method directly. The Razor compiler will emit the appropriate calls to this method
    /// for each use of the <c>@section</c> directive in your .cshtml file.
    /// </remarks>
    /// <param name="name">The name of the section.</param>
    /// <param name="section">The section delegate.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> or <paramref name="section"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a section with this name is already defined.</exception>
    protected virtual void DefineSection(string name, Func<Task> section)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(section);

        _sectionWriters ??= new();

        if (_sectionWriters.ContainsKey(name))
        {
            throw new InvalidOperationException("Section already defined.");
        }
        _sectionWriters[name] = section;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="sectionName">The name of the section.</param>
    /// <param name="section">The section delegate.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    protected void DefineSection(string sectionName, Func<object?, Task> section)
        => DefineSection(sectionName, () => section(null /* writer */));

    /// <summary>
    /// Renders the section with the specified name.
    /// </summary>
    /// <param name="sectionName">The section name.</param>
    /// <param name="required">Whether the section is required or not.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> representing the rendering of the section.</returns>
    /// <exception cref="ArgumentException">Thrown when no section with name <paramref name="sectionName"/> has been defined by the slice being rendered.</exception>
    /// <exception cref="NotImplementedException"></exception>
    protected ValueTask<HtmlString> RenderSectionAsync(string sectionName, bool required)
    {
        var sectionDefined = _sectionWriters?.ContainsKey(sectionName) != false;
        if (required && !sectionDefined)
        {
            throw new ArgumentException($"The section '{sectionName}' has not been declared by the slice being rendered.");
        }
        else if (!required && !sectionDefined)
        {
            return ValueTask.FromResult(HtmlString.Empty);
        }

        //throw new NotImplementedException("Haven't implemented layouts yet, but will!");

        var section = _sectionWriters![sectionName];
        var task = section();
        if (task.IsCompletedSuccessfully)
        {
            task.GetAwaiter().GetResult();
        }
        return ValueTask.FromResult(HtmlString.Empty);
    }

    /// <summary>
    /// Writes a string value to the output without HTML encoding it.
    /// </summary>
    /// <remarks>
    /// You generally shouldn't call this method directly. The Razor compiler will emit the appropriate calls to this method for
    /// all blocks of HTML in your .cshtml file.
    /// </remarks>
    /// <param name="value">The value to write to the output.</param>
    protected void WriteLiteral(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        _bufferWriter?.WriteHtml(value.AsSpan());
        _textWriter?.Write(value);
    }

    /// <summary>
    /// Writes the string representation of the provided object to the output without HTML encoding it.
    /// </summary>
    /// <remarks>
    /// You generally shouldn't call this method directly. The Razor compiler will emit the appropriate calls to this method for
    /// all blocks of HTML in your .cshtml file.
    /// </remarks>
    /// <param name="value">The value to write to the output.</param>
    protected void WriteLiteral<T>(T? value)
    {
        if (value is ISpanFormattable)
        {
            WriteSpanFormattable((ISpanFormattable)(object)value, htmlEncode: false);
            return;
        }
        WriteLiteral(value?.ToString());
    }

    /// <summary>
    /// Writes a buffer of UTF8 bytes to the output without HTML encoding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// You generally shouldn't call this method directly. The Razor compiler will emit the appropriate calls to this method for
    /// all blocks of HTML in your .cshtml file.
    /// </para>
    /// <para>
    /// NOTE: We'd need a tweak to the Razor compiler to to have it support emitting <see cref="WriteLiteral(ReadOnlySpan{byte})"/> calls with UTF8 string literals
    ///       i.e. https://github.com/dotnet/razor/issues/8429
    /// </para>
    /// </remarks>
    /// <param name="value">The value to write to the output.</param>
    protected void WriteLiteral(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return;
        }

        _bufferWriter?.Write(value);

        if (_textWriter is not null)
        {
            var charCount = Encoding.Unicode.GetCharCount(value);
            var buffer = ArrayPool<char>.Shared.Rent(charCount);
            var bytesDecoded = Encoding.Unicode.GetChars(value, buffer);

            Debug.Assert(bytesDecoded == value.Length, "Bad decoding when writing to TextWriter in WriteLiteral(ReadOnlySpan<byte>)");

            _textWriter.Write(buffer, 0, charCount);
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes a <see cref="bool"/> value to the output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// You generally shouldn't call this method directly. The Razor compiler will emit the appropriate calls to this method for
    /// all matching Razor expressions in your .cshtml file.
    /// </para>
    /// <para>
    /// To manually write out a value, use <see cref="WriteBool"/> instead, e.g. <c>@WriteBool(todo.Complete)</c>
    /// </para>
    /// </remarks>
    /// <param name="value"></param>
    protected void Write(bool? value) => WriteBool(value);

    /// <summary>
    /// Writes a buffer of UTF8 bytes to the output after HTML encoding it.
    /// </summary>
    /// <remarks>
    /// You generally shouldn't call this method directly. The Razor compiler will emit the appropriate calls to this method for
    /// all matching Razor expressions in your .cshtml file.
    /// </remarks>
    /// <param name="value">The value to write to the output.</param>
    protected void Write(byte[] value) => Write(value.AsSpan());

    /// <summary>
    /// Writes a buffer of UTF8 bytes to the output after HTML encoding it.
    /// </summary>
    /// <remarks>
    /// You generally shouldn't call this method directly. The Razor compiler will emit the appropriate calls to this method for
    /// all matching Razor expressions in your .cshtml file.
    /// </remarks>
    /// <param name="value">The value to write to the output.</param>
    protected void Write(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            return;
        }

        _bufferWriter?.HtmlEncodeAndWriteUtf8(value, _htmlEncoder);

        if (_textWriter is not null)
        {
            var charCount = Encoding.UTF8.GetCharCount(value);
            var buffer = ArrayPool<char>.Shared.Rent(charCount);
            var bytesDecoded = Encoding.UTF8.GetChars(value, buffer);

            Debug.Assert(bytesDecoded == value.Length, "Bad decoding when writing to TextWriter in Write(ReadOnlySpan<byte>)");
            
            _htmlEncoder.Encode(_textWriter, buffer, 0, charCount);
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes the specified <see cref="HtmlString"/> value to the output without HTML encoding it again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// You generally shouldn't call this method directly. The Razor compiler will emit the appropriate calls to this method for
    /// all matching Razor expressions in your .cshtml file.
    /// </para>
    /// <para>
    /// To manually write out a value, use <see cref="WriteHtml{T}(T)"/> instead,
    /// e.g. <c>@WriteHtmlContent(myCustomHtmlString)</c>
    /// </para>
    /// </remarks>
    /// <param name="htmlString">The <see cref="HtmlString"/> value to write to the output.</param>
    protected void Write(HtmlString htmlString)
    {
        if (htmlString is not null && htmlString != HtmlString.Empty)
        {
            WriteHtml(htmlString);
        }
    }

    /// <summary>
    /// Writes the specified <see cref="IHtmlContent"/> value to the output without HTML encoding it again.
    /// </summary>
    /// <param name="htmlContent">The <see cref="IHtmlContent"/> value to write to the output.</param>
    protected void Write(IHtmlContent? htmlContent)
    {
        if (htmlContent is not null)
        {
            WriteHtml(htmlContent);
        }
    }

    /// <summary>
    /// Writes the specified value to the output after HTML encoding it.
    /// </summary>
    /// <param name="value">The value to write to the output.</param>
    protected void Write(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Write(value.AsSpan());
        }
    }

    /// <summary>
    /// Writes the specified value to the output after HTML encoding it.
    /// </summary>
    /// <param name="value">The value to write to the output.</param>
    protected void Write(ReadOnlySpan<char> value)
    {
        if (value.Length > 0)
        {
            _bufferWriter?.HtmlEncodeAndWrite(value, _htmlEncoder);
            if (_textWriter is not null)
            {
                var buffer = ArrayPool<char>.Shared.Rent(value.Length);
                value.CopyTo(buffer);
                _htmlEncoder.Encode(_textWriter, buffer, 0, value.Length);
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// Writes the specified <typeparamref name="T"/> to the output.
    /// </summary>
    /// <remarks>
    /// You generally shouldn't call this method directly. The Razor compiler will emit calls to the most appropriate overload of
    /// the <c>Write</c> method for all Razor expressions in your .cshtml file, e.g. <c>@someVariable</c>.
    /// </remarks>
    /// <param name="value">The <typeparamref name="T"/> to write to the output.</param>
    protected void Write<T>(T? value)
    {
        WriteValue(value);
    }

    /// <summary>
    /// Writes a <see cref="bool"/> value to the output.
    /// </summary>
    /// <param name="value">The value to write to the output.</param>
    /// <returns><see cref="HtmlString.Empty"/> to allow for easy calling via a Razor expression, e.g. <c>@WriteBool(todo.Completed)</c></returns>
    protected HtmlString WriteBool(bool? value)
    {
        if (value.HasValue)
        {
            _bufferWriter?.Write(value.Value);
            _textWriter?.Write(value.Value);
        }
        return HtmlString.Empty;
    }

    /// <summary>
    /// Write the specified <see cref="ISpanFormattable"/> value to the output with the specified format and optional <see cref="IFormatProvider" />.
    /// </summary>
    /// <param name="formattable">The value to write to the output.</param>
    /// <param name="format">The format to use when writing the value to the output. Defaults to the default format for the value's type if not provided.</param>
    /// <param name="formatProvider">The <see cref="IFormatProvider" /> to use when writing the value to the output. Defaults to <see cref="CultureInfo.CurrentCulture"/> if <c>null</c>.</param>
    /// <param name="htmlEncode">Whether to HTML encode the value or not. Defaults to <c>true</c>.</param>
    /// <returns><see cref="HtmlString.Empty"/> to allow for easy calling via a Razor expression, e.g. <c>@WriteSpanFormattable(item.DueBy, "d")</c></returns>
    protected HtmlString WriteSpanFormattable<T>(T? formattable, ReadOnlySpan<char> format = default, IFormatProvider? formatProvider = null, bool htmlEncode = true)
        where T : ISpanFormattable
    {
        if (formattable is not null)
        {
            var htmlEncoder = htmlEncode ? _htmlEncoder : NullHtmlEncoder.Default;
            _bufferWriter?.HtmlEncodeAndWriteSpanFormattable(formattable, htmlEncoder, format, formatProvider);
            _textWriter?.HtmlEncodeAndWriteSpanFormattable(formattable, htmlEncoder, format, formatProvider);
        }

        return HtmlString.Empty;
    }

    /// <summary>
    /// Writes the specified <see cref="IHtmlContent"/> value to the output.
    /// </summary>
    /// <param name="htmlContent">The <see cref="IHtmlContent"/> value to write to the output.</param>
    /// <returns><see cref="HtmlString.Empty"/> to allow for easy calling via a Razor expression, e.g. <c>@WriteHtmlContent(myCustomHtmlContent)</c></returns>
    protected HtmlString WriteHtml<T>(T htmlContent)
        where T : IHtmlContent
    {
        if (htmlContent is not null)
        {
            if (_bufferWriter is not null)
            {
                _utf8BufferTextWriter ??= Utf8BufferTextWriter.Get(_bufferWriter);
                htmlContent.WriteTo(_utf8BufferTextWriter, _htmlEncoder);
            }
            if (_textWriter is not null)
            {
                htmlContent.WriteTo(_textWriter, _htmlEncoder);
            }
        }

        return HtmlString.Empty;
    }

    /// <summary>
    /// Writes the specified <see cref="HtmlString"/> value to the output.
    /// </summary>
    /// <param name="htmlstring">The <see cref="HtmlString"/> value to write to the output.</param>
    /// <returns><see cref="HtmlString.Empty"/> to allow for easy calling via a Razor expression, e.g. <c>@WriteHtmlContent(myCustomHtmlContent)</c></returns>
    protected HtmlString WriteHtml(HtmlString htmlstring)
    {
        if (htmlstring is not null && htmlstring != HtmlString.Empty)
        {
            _bufferWriter?.WriteHtml(htmlstring.Value);
        }

        return HtmlString.Empty;
    }

    private void WriteValue<T>(T value)
    {
        if (value is null)
        {
            return;
        }

        // Dispatch to the most appropriately typed method
        if (TryWriteFormattableValue(value))
        {
            return;
        }

        if (value is string)
        {
            Write((string)(object)value);
        }
        else if (value is byte[])
        {
            Write(((byte[])(object)value).AsSpan());
        }
        // Handle derived types (this currently results in value types being boxed)
        else if (value is ISpanFormattable)
        {
            WriteSpanFormattable((ISpanFormattable)(object)value, default, null);
        }
        else if (value is HtmlString)
        {
            WriteHtml((HtmlString)(object)value);
        }
        else if (value is IHtmlContent)
        {
            WriteHtml((IHtmlContent)(object)value);
        }
#if NET8_0_OR_GREATER
        else if (value is Enum)
        {
            WriteSpanFormattable((Enum)(object)value);
        }
#endif
        // Fallback to ToString()
        else
        {
            Write(value?.ToString());
        }
    }

    /// <summary>
    /// Disposes the instance. Overriding implementations should ensure they call <c>base.Dispose()</c> after performing their
    /// custom dispose logic, e.g.:
    /// <code>
    /// public override void Dispose()
    /// {
    ///     // Custom dispose logic here...
    ///     base.Dispose();
    /// }
    /// </code>
    /// </summary>
    public virtual void Dispose()
    {
        ReturnPooledObjects();
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReturnPooledObjects()
    {
        if (_utf8BufferTextWriter is not null)
        {
            Utf8BufferTextWriter.Return(_utf8BufferTextWriter);
        }
    }
}
