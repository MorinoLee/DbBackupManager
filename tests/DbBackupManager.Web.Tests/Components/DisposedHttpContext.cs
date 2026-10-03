using System.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace DbBackupManager.Web.Tests.Components;

internal static class DisposedHttpContext
{
    public static HttpContext Create() => new DefaultHttpContext(new DisposedFeatureCollection());

    private sealed class DisposedFeatureCollection : IFeatureCollection
    {
        public object? this[Type key]
        {
            get => throw CreateDisposed();
            set => throw CreateDisposed();
        }

        public bool IsReadOnly => true;

        public int Revision => 0;

        public TFeature? Get<TFeature>() => throw CreateDisposed();

        public void Set<TFeature>(TFeature? instance) => throw CreateDisposed();

        public IEnumerator<KeyValuePair<Type, object>> GetEnumerator() => throw CreateDisposed();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private static ObjectDisposedException CreateDisposed() =>
            new("Collection", "IFeatureCollection has been disposed.");
    }
}
