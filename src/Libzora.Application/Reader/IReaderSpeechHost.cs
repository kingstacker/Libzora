using Libzora.ApplicationServices.Tts;
using Libzora.Core;

namespace Libzora.ApplicationServices;

/// <summary>Speech text and highlights without depending on a UI control.</summary>
public interface IReaderSpeechHost : IReaderHost
{
    ReaderTtsTextSnapshot? GetSpeechText();
    void SetSpeechHighlight(int start, int length);
    void ClearSpeechHighlight();
    void NavigateToSpeechOffset(int offset) { }
}
