/*
   Copyright 2014 David Bordoley

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;

namespace SQLitePCL.pretty
{
    /// <summary>
    /// Extensions methods for <see cref="IAsyncStatement"/>.
    /// </summary>
    public static class AsyncStatement
    {
        /// <summary>
        /// Schedules the <see cref="Action"/> <paramref name="f"/> on the statement's operations queue.
        /// </summary>
        /// <param name="This">The async statement.</param>
        /// <param name="f">The action.</param>
        /// <param name="cancellationToken">Cancellation token that can be used to cancel the task.</param>
        /// <returns>A task that completes when <paramref name="f"/> returns.</returns>
        public static Task Use(
            this IAsyncStatement This,
            Action<IStatement> f,
            CancellationToken cancellationToken)
        {
            Contract.Requires(This != null);
            Contract.Requires(f != null);

            return This.Use(conn =>
            {
                f(conn);
                return Enumerable.Empty<Unit>();
            }, cancellationToken);
        }

        /// <summary>
        /// Schedules the <see cref="Action"/> <paramref name="f"/> on the statement's operations queue.
        /// </summary>
        /// <param name="This">The async statement.</param>
        /// <param name="f">The action.</param>
        /// <returns>A task that completes when <paramref name="f"/> returns.</returns>
        public static Task Use(this IAsyncStatement This, Action<IStatement> f)
        {
            return Use(This, f, CancellationToken.None);
        }

        /// <summary>
        /// Schedules the <see cref="Func&lt;T,TResult&gt;"/> <paramref name="f"/> on the statement's operations queue.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="This">The async statement.</param>
        /// <param name="f">A function from <see cref="IAsyncStatement"/> to <typeparamref name="T"/>.</param>
        /// <param name="cancellationToken">Cancellation token that can be used to cancel the task.</param>
        /// <returns>A task that completes with the result of <paramref name="f"/>.</returns>
        public static Task<T> Use<T>(
            this IAsyncStatement This,
            Func<IStatement, T> f,
            CancellationToken cancellationToken)
        {
            Contract.Requires(This != null);
            Contract.Requires(f != null);

            return This.Use(conn => new[] { f(conn) }).ToTask(cancellationToken);
        }

        /// <summary>
        /// Schedules the <see cref="Func&lt;T,TResult&gt;"/> <paramref name="f"/> on the statement's operations queue.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="This">The async statement.</param>
        /// <param name="f">A function from <see cref="IAsyncStatement"/> to <typeparamref name="T"/>.</param>
        /// <returns>A task that completes with the result of <paramref name="f"/>.</returns>
        public static Task<T> Use<T>(this IAsyncStatement This, Func<IStatement, T> f)
        {
            return Use(This, f, CancellationToken.None);
        }

        /// <summary>
        /// Executes the <see cref="IStatement"/> with provided bind parameter values.
        /// </summary>
        /// <param name="This">The async statement.</param>
        /// <param name="cancellationToken">Cancellation token that can be used to cancel the task.</param>
        /// <param name="values">The position indexed values to bind.</param>
        /// <returns>A <see cref="Task"/> that completes when the statement is executed.</returns>
        public static Task ExecuteAsync(
            this IAsyncStatement This,
            CancellationToken cancellationToken,
            params object[] values)
        {
            return This.Use(stmt => { stmt.Execute(values); }, cancellationToken);
        }

        /// <summary>
        /// Executes the <see cref="IStatement"/> with provided bind parameter values.
        /// </summary>
        /// <param name="This">The async statement.</param>
        /// <param name="values">The position indexed values to bind.</param>
        /// <returns>A <see cref="Task"/> that completes when the statement is executed.</returns>
        public static Task ExecuteAsync(
            this IAsyncStatement This,
            params object[] values)
        {
            return This.ExecuteAsync(CancellationToken.None, values);
        }
    }

    internal class AsyncStatementImpl : IAsyncStatement
    {
        private readonly IStatement stmt;
        private readonly IAsyncDatabaseConnection conn;

        private bool disposed = false;

        internal AsyncStatementImpl(IStatement stmt, IAsyncDatabaseConnection conn)
        {
            this.stmt = stmt;
            this.conn = conn;
        }

        public IObservable<T> Use<T>(Func<IStatement, IEnumerable<T>> f)
        {
            if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }

            return Observable.Create((IObserver<T> observer, CancellationToken cancellationToken) =>
                {
                    // Prevent calls to subscribe after the statement is disposed
                    if (this.disposed)
                    {
                        observer.OnError(new ObjectDisposedException(this.GetType().FullName));
                        return Task.FromResult(Unit.Default);
                    }

                    return conn.Use(_ =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Note: Diposing the statement wrapper doesn't dispose the underlying statement
                            // The intent here is to prevent access to the underlying statement outside of the
                            // function call.
                            using (var stmt = new StatementWrapper(this.stmt))
                            {
                                foreach (var e in f(stmt))
                                {
                                    observer.OnNext(e);
                                    cancellationToken.ThrowIfCancellationRequested();
                                }
                            }
                        }, cancellationToken);
                });
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            conn.Use(_ =>
                {
                    stmt.Dispose();
                });
        }

        private sealed class StatementWrapper : IStatement
        {
            private readonly IStatement stmt;

            private bool disposed = false;

            internal StatementWrapper(IStatement stmt)
            {
                this.stmt = stmt;
            }

            public IReadOnlyOrderedDictionary<string, IBindParameter> BindParameters
            {
                get
                {
                    if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }

                    // FIXME: If someone keeps a reference to this list it leaks the implementation out
                    return stmt.BindParameters;
                }
            }

            public IReadOnlyList<ColumnInfo> Columns
            {
                get
                {
                    if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }

                    // FIXME: If someone keeps a reference to this list it leaks the implementation out
                    return stmt.Columns;
                }
            }

            public string SQL
            {
                get
                {
                    if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }
                    return stmt.SQL;
                }
            }

            public bool IsReadOnly
            {
                get
                {
                    if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }
                    return stmt.IsReadOnly;
                }
            }

            public bool IsBusy
            {
                get
                {
                    if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }
                    return stmt.IsBusy;
                }
            }

            public void ClearBindings()
            {
                if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }
                stmt.ClearBindings();
            }

            public IReadOnlyList<IResultSetValue> Current
            {
                get
                {
                    if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }

                    // FIXME: If someone keeps a reference to this list it leaks the implementation out
                    return stmt.Current;
                }
            }

            object IEnumerator.Current
            {
                get
                {
                    return this.Current;
                }
            }

            public bool MoveNext()
            {
                if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }
                return stmt.MoveNext();
            }

            public void Reset()
            {
                if (disposed) { throw new ObjectDisposedException(this.GetType().FullName); }
                stmt.Reset();
            }

            public void Dispose()
            {
                // Guard against someone taking a reference to this and trying to use it outside of
                // the Use function delegate
                disposed = true;
                // We don't actually own the statement so its not disposed
            }
        }
    }
}
