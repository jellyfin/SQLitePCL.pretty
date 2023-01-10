/*
   Copyright 2014 David Bordoley
   Copyright 2014 Zumero, LLC

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
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SQLitePCL.pretty.Tests
{
    public class SQLiteDatabaseConnectionTests
    {
        static SQLiteDatabaseConnectionTests()
        {
            Batteries_V2.Init();
        }

        private static string GetTempFile()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                return "tmp" + db.Query("SELECT lower(hex(randomblob(16)));").SelectScalarString().First();
            }
        }

        [Fact]
        public void TestFinalize()
        {
            // There is no way to assert, so this test is primarily for code coverage.
            var db = SQLite3.OpenInMemory();
            _ = db.PrepareStatement("SELECT 1;");

            GC.Collect();
        }

        [Fact]
        public void TestDispose()
        {
            var db = SQLite3.OpenInMemory();

            // This is for test purposes only, never prepare a statement and dispose the db before the statements.
            db.PrepareStatement("Select 1");
            db.PrepareStatement("Select 2");

            db.Dispose();

            Assert.Throws<ObjectDisposedException>(() => { var x = db.Changes; });
            Assert.Throws<ObjectDisposedException>(() => { var x = db.TotalChanges; });
            Assert.Throws<ObjectDisposedException>(() => { var x = db.IsAutoCommit; });
            Assert.Throws<ObjectDisposedException>(() => { var x = db.IsReadOnly; });
            Assert.Throws<ObjectDisposedException>(() => { var x = db.LastInsertedRowId; });
            Assert.Throws<ObjectDisposedException>(() => { var x = db.Statements; });

            using (var db2 = SQLite3.OpenInMemory())
            {
                Assert.Throws<ObjectDisposedException>(() => { db.Backup("main", db2, "main"); });
            }

            Assert.Throws<ObjectDisposedException>(() => { var x = db.IsDatabaseReadOnly("main"); });
            Assert.Throws<ObjectDisposedException>(() => { var x = db.GetFileName("main"); });
            Assert.Throws<ObjectDisposedException>(() => { var x = db.OpenBlob("db", "tn", "cn", 0, false); });
            Assert.Throws<ObjectDisposedException>(() => { var x = db.PrepareStatement("SELECT 1"); });

            Assert.Throws<ObjectDisposedException>(() => { db.Status(DatabaseConnectionStatusCode.CacheMiss, out int current, out int highwater, false); });

            Assert.Throws<ObjectDisposedException>(() => { db.WalCheckPoint("main"); });
        }

        [Fact]
        public void TestIsDatabaseReadonly()
        {
            using (var db = SQLite3.Open(":memory:", ConnectionFlags.ReadOnly, null))
            {
                Assert.True(db.IsReadOnly);
                Assert.True(db.IsDatabaseReadOnly("main"));
                Assert.Throws<ArgumentException>(() => db.IsDatabaseReadOnly("baz"));
            }

            using (var db = SQLite3.OpenInMemory())
            {
                Assert.False(db.IsDatabaseReadOnly("main"));
            }
        }

        [Fact]
        public void TestInterrupt()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                db.Execute("CREATE TABLE foo (x int);");
                db.Execute("INSERT INTO foo (x) VALUES (1);");
                db.Execute("INSERT INTO foo (x) VALUES (2);");
                db.Execute("INSERT INTO foo (x) VALUES (3);");

                using (var stmt = db.PrepareStatement("SELECT x FROM foo;"))
                {
                    stmt.MoveNext();
                    db.Interrupt();
                    Assert.Throws<OperationCanceledException>(() => stmt.MoveNext());
                }
            }
        }

        [Fact]
        public void TestRollbackEvent()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                var rollbacks = 0;

                db.Rollback += (o, e) => rollbacks++;
                Assert.Equal(0, rollbacks);

                db.ExecuteAll(
                    @"CREATE TABLE foo (x int);
                      INSERT INTO foo (x) VALUES (1);
                      BEGIN TRANSACTION;
                      INSERT INTO foo (x) VALUES (2);
                      ROLLBACK TRANSACTION;
                      BEGIN TRANSACTION;
                      INSERT INTO foo (x) VALUES (2);
                      ROLLBACK TRANSACTION;");

                Assert.Equal(2, rollbacks);
            }
        }

        [Fact]
        public void TestProfileEvent()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                var statement = "CREATE TABLE foo (x int);";
                db.Profile += (o, e) =>
                    {
                        Assert.Equal(e.Statement, statement);
                        Assert.True(TimeSpan.MinValue < e.ExecutionTime);
                    };

                db.Execute(statement);
            }
        }

        [Fact]
        public void TestTraceEvent()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                var statement = "CREATE TABLE foo (x int);";
                db.Trace += (o, e) =>
                    {
                        Assert.Equal(e.Statement, statement);
                    };

                db.Execute(statement);

                statement = "INSERT INTO foo (x) VALUES (1);";
                db.Execute(statement);
            }
        }

        [Fact]
        public void TestUpdateEvent()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                var currentAction = ActionCode.CreateTable;
                var rowid = 1;

                db.Update += (o, e) =>
                    {
                        Assert.Equal(e.Action, currentAction);
                        Assert.Equal("main", e.Database);
                        Assert.Equal("foo", e.Table);
                        Assert.Equal(e.RowId, rowid);
                    };

                currentAction = ActionCode.CreateTable;
                rowid = 1;
                db.Execute("CREATE TABLE foo (x int);");

                currentAction = ActionCode.Insert;
                rowid = 1;
                db.Execute("INSERT INTO foo (x) VALUES (1);");

                currentAction = ActionCode.Insert;
                rowid = 2;
                db.Execute("INSERT INTO foo (x) VALUES (2);");

                currentAction = ActionCode.DropTable;
                rowid = 2;
                db.Execute("DROP TABLE foo");
            }
        }

        [Fact]
        public void TestChanges()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                Assert.Equal(0, db.TotalChanges);
                Assert.Equal(0, db.Changes);

                db.Execute("CREATE TABLE foo (x int);");
                Assert.Equal(0, db.TotalChanges);
                Assert.Equal(0, db.Changes);

                db.Execute("INSERT INTO foo (x) VALUES (1);");
                Assert.Equal(1, db.TotalChanges);
                Assert.Equal(1, db.Changes);

                db.Execute("INSERT INTO foo (x) VALUES (2);");
                db.Execute("INSERT INTO foo (x) VALUES (3);");
                Assert.Equal(3, db.TotalChanges);
                Assert.Equal(1, db.Changes);

                db.Execute("UPDATE foo SET x=5;");
                Assert.Equal(6, db.TotalChanges);
                Assert.Equal(3, db.Changes);
            }
        }

        [Fact]
        public void TestIsAutoCommit()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                Assert.True(db.IsAutoCommit);
                db.Execute("BEGIN TRANSACTION;");
                Assert.False(db.IsAutoCommit);
            }
        }

        [Fact]
        public void TestSetBusyTimeout()
        {
            var builder = SQLiteDatabaseConnectionBuilder.InMemory.With(busyTimeout: new TimeSpan(100));

            using (var db = builder.Build())
            {
                Assert.NotNull(db);
                // FIXME: Not the best test without Asserts.
            }
        }

        [Fact]
        public void TestStatements()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                Assert.Empty(db.Statements);

                using (IStatement stmt0 = db.PrepareStatement("SELECT 5;"),
                                  stmt1 = db.PrepareStatement("SELECT 6;"),
                                  stmt2 = db.PrepareStatement("SELECT 7;"))
                {
                    Assert.Equal(3, db.Statements.Count());

                    IStatement[] stmts = { stmt2, stmt1, stmt0 };

                    // IStatement can't sanely implement equality at the
                    // interface level. Doing so would tightly bind the
                    // interface to the underlying SQLite implementation
                    // which we don't want to do.
                    foreach (var pair in Enumerable.Zip(stmts, db.Statements, (a, b) => Tuple.Create(a.SQL, b.SQL)))
                    {
                        Assert.Equal(pair.Item2, pair.Item1);
                    }
                }
            }
        }

        // FIXME: This test creates a file which isn't PCL friendly. Need to update.

        [Fact]
        public void TestTryGetFileName()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                db.Execute("CREATE TABLE foo (x int);");
                Assert.False(db.TryGetFileName("foo", out string filename));
                Assert.True(filename == null);

                Assert.Throws<InvalidOperationException>(() => db.GetFileName("main"));
            }

            var tempFile = GetTempFile();
            using (var db = SQLite3.Open(tempFile))
            {
                db.Execute("CREATE TABLE foo (x int);");
                Assert.True(db.TryGetFileName("main", out string filename));
                Assert.EndsWith(tempFile, filename);
                Assert.Equal(filename, db.GetFileName("main"));
            }
            raw.sqlite3__vfs__delete(null, tempFile, 1);
        }

        [Fact]
        public void TestWithCollation()
        {
            var builder = SQLiteDatabaseConnectionBuilder.InMemory.WithCollation("e2a", (string s1, string s2) =>
                {
                    s1 = s1.Replace('e', 'a');
                    s2 = s2.Replace('e', 'a');
                    return string.CompareOrdinal(s1, s2);
                });

            using (var db = builder.Build())
            {
                db.Execute("CREATE TABLE foo (x text COLLATE e2a);");
                db.Execute("INSERT INTO foo (x) VALUES ('b')");
                db.Execute("INSERT INTO foo (x) VALUES ('c')");
                db.Execute("INSERT INTO foo (x) VALUES ('d')");
                db.Execute("INSERT INTO foo (x) VALUES ('e')");
                db.Execute("INSERT INTO foo (x) VALUES ('f')");

                string top =
                    db.Query("SELECT x FROM foo ORDER BY x ASC LIMIT 1;").SelectScalarString().First();

                Assert.Equal("e", top);
            }

            builder = builder.WithoutCollation("e2a");
            using (var db = builder.Build())
            {
                Assert.Throws<SQLiteException>(() => db.Execute("CREATE TABLE bar (x text COLLATE e2a);"));
            }
        }

        [Fact]
        public void TestWithCommitHook()
        {
            var commits = 0;
            var tmpFile = GetTempFile();
            var builder =
                SQLiteDatabaseConnectionBuilder.Create(tmpFile,
                    commitHook: () =>
                        {
                            commits++;
                            return false;
                        });

            using (var db = builder.Build())
            {
                db.Execute("CREATE TABLE foo (x int);");
                db.Execute("INSERT INTO foo (x) VALUES (1);");

                Assert.Equal(2, commits);
            }

            builder = builder.Without(commitHook: true);
            using (var db = builder.Build())
            {
                db.Execute("INSERT INTO foo (x) VALUES (1);");
                db.Execute("INSERT INTO foo (x) VALUES (1);");
                Assert.Equal(2, commits);
            }

            builder = builder.With(commitHook: () => true);
            using (var db = builder.Build())
            {
                try
                {
                    db.Execute("INSERT INTO foo (x) VALUES (1);");
                    Assert.True(false, "Expected exception to be thrown");
                }
                catch (SQLiteException e)
                {
                    Assert.Equal(ErrorCode.ConstraintCommitHook, e.ExtendedErrorCode);
                }

                var count =
                    db.Query("SELECT COUNT(*) from foo")
                        .Select(row => row.First().ToInt())
                        .First();
                Assert.Equal(3, count);
            }

            raw.sqlite3__vfs__delete(null, tmpFile, 1);
        }

        [Fact]
        public void TestWithAggregateFunc()
        {
            var builder = SQLiteDatabaseConnectionBuilder.InMemory.WithAggregateFunc(
                "sum_plus_count",
                Tuple.Create(0L, 0L),
                (Tuple<long, long> acc, ISQLiteValue arg) => Tuple.Create(acc.Item1 + arg.ToInt64(), acc.Item2 + 1L),
                (Tuple<long, long> acc) => (acc.Item1 + acc.Item2).ToSQLiteValue());

            using (var db = builder.Build())
            {
                db.Execute("CREATE TABLE foo (x int);");
                for (int i = 0; i < 5; i++)
                {
                    db.Execute("INSERT INTO foo (x) VALUES (?);", i);
                }
                long c = db.Query("SELECT sum_plus_count(x) FROM foo;").SelectScalarInt64().First();
                Assert.Equal((0 + 1 + 2 + 3 + 4) + 5, c);
            }

            builder = builder.WithoutFunc("sum_plus_count", 1);
            using (var db = builder.Build())
            {
                db.Execute("CREATE TABLE foo (x int);");
                for (int i = 0; i < 5; i++)
                {
                    db.Execute("INSERT INTO foo (x) VALUES (?);", i);
                }

                Assert.Throws<SQLiteException>(() =>
                    db.Query("SELECT sum_plus_count(x) FROM foo;").SelectScalarInt64().First());
            }

            builder = builder
                .WithAggregateFunc("row_count", 0, i => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (int i, ISQLiteValue _v0) => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (i, _v0, _v1) => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (i, _v0, _v1, _v2) => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (i, _v0, _v1, _v2, _v3) => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (i, _v0, _v1, _v2, _v3, _v4) => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (i, _v0, _v1, _v2, _v3, _v4, _v5) => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (i, _v0, _v1, _v2, _v3, _v4, _v5, _v6) => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (i, _v0, _v1, _v2, _v3, _v4, _v5, _v6, _v7) => i + 1, i => i.ToSQLiteValue())
                .WithAggregateFunc("row_count", 0, (int i, IReadOnlyList<ISQLiteValue> v) => i + 1, i => i.ToSQLiteValue());

            using (var db = builder.Build())
            {
                db.Execute("CREATE TABLE foo (x int);");

                using (var stmt = db.PrepareStatement("INSERT INTO foo (x) VALUES (?);"))
                {
                    for (int i = 0; i < 5; i++)
                    {
                        stmt.Execute(1);
                    }
                }

                var result = db.Query("SELECT row_count() FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x, 1) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x, 1, 2) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x, 1, 2, 3) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x, 1, 2, 3, 4) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x, 1, 2, 3, 4, 5) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x, 1, 2, 3, 4, 5, 6) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x, 1, 2, 3, 4, 5, 6, 7) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);

                result = db.Query("SELECT row_count(x, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11) FROM foo;").SelectScalarInt64().First();
                Assert.Equal(5, result);
            }
        }

        [Fact]
        public void TestWithScalarFunc()
        {
            var builder = SQLiteDatabaseConnectionBuilder.InMemory.WithScalarFunc(
                "count_nulls",
                (IReadOnlyList<ISQLiteValue> vals) =>
                    vals.Count(val => val.SQLiteType == SQLiteType.Null).ToSQLiteValue());

            using (var db = builder.Build())
            {
                Assert.Equal(0, db.Query("SELECT count_nulls(1,2,3,4,5,6,7,8);").SelectScalarInt().First());
                Assert.Equal(0, db.Query("SELECT count_nulls();").SelectScalarInt().First());
                Assert.Equal(1, db.Query("SELECT count_nulls(null);").SelectScalarInt().First());
                Assert.Equal(2, db.Query("SELECT count_nulls(1,null,3,null,5);").SelectScalarInt().First());
            }

            builder = builder.WithoutFunc("count_nulls", -1);
            using (var db = builder.Build())
            {
                Assert.Throws<SQLiteException>(() =>
                    db.Query("SELECT count_nulls(1,2,3,4,5,6,7,8);")
                    .Select(row => row[0].ToInt64())
                    .First());
            }
            builder = builder
                .WithScalarFunc("count_args", (IReadOnlyList<ISQLiteValue> values) => values.Count.ToSQLiteValue())
                .WithScalarFunc("len_as_blobs", (IReadOnlyList<ISQLiteValue> values) =>
                    values.Where(v => v.SQLiteType != SQLiteType.Null).Aggregate(0, (acc, val) => acc + val.Length).ToSQLiteValue())
                .WithScalarFunc("my_concat", (IReadOnlyList<ISQLiteValue> values) =>
                    string.Join("", values.Select(v => v.ToString())).ToSQLiteValue())
                .WithScalarFunc("my_mean", (IReadOnlyList<ISQLiteValue> values) =>
                    (values.Aggregate(0d, (acc, v) => acc + v.ToDouble()) / values.Count).ToSQLiteValue())
                .WithScalarFunc("makeblob", (ISQLiteValue v) =>
                    {
                        byte[] b = new byte[v.ToInt()];
                        for (int i = 0; i < b.Length; i++)
                        {
                            b[i] = (byte)(i % 256);
                        }
                        return b.ToSQLiteValue();
                    })
                .WithScalarFunc("cube", (ISQLiteValue x) => (x.ToInt64() * x.ToInt64() * x.ToInt64()).ToSQLiteValue())
                .WithScalarFunc("num_var", () => (0).ToSQLiteValue())
                .WithScalarFunc("num_var", (ISQLiteValue _1) => (1).ToSQLiteValue())
                .WithScalarFunc("num_var", (_1, _2) => (2).ToSQLiteValue())
                .WithScalarFunc("num_var", (_1, _2, _3) => (3).ToSQLiteValue())
                .WithScalarFunc("num_var", (_1, _2, _3, _4) => (4).ToSQLiteValue())
                .WithScalarFunc("num_var", (_1, _2, _3, _4, _5) => (5).ToSQLiteValue())
                .WithScalarFunc("num_var", (_1, _2, _3, _4, _5, _6) => (6).ToSQLiteValue())
                .WithScalarFunc("num_var", (_1, _2, _3, _4, _5, _6, _7) => (7).ToSQLiteValue())
                .WithScalarFunc("num_var", (_1, _2, _3, _4, _5, _6, _7, _8) => (8).ToSQLiteValue())
                .WithScalarFunc("zeroblob", (ISQLiteValue i) => SQLiteValue.ZeroBlob(i.ToInt()))
                .WithScalarFunc("nullFunc", () => SQLiteValue.Null);

            using (var db = builder.Build())
            {

                Assert.Equal(8, db.Query("SELECT count_args(1,2,3,4,5,6,7,8);").SelectScalarInt().First());
                Assert.Equal(0, db.Query("SELECT count_args();").SelectScalarInt().First());
                Assert.Equal(1, db.Query("SELECT count_args(null);").SelectScalarInt().First());

                Assert.Equal(0, db.Query("SELECT len_as_blobs();").SelectScalarInt().First());
                Assert.Equal(0, db.Query("SELECT len_as_blobs(null);").SelectScalarInt().First());
                Assert.True(8 <= db.Query("SELECT len_as_blobs(1,2,3,4,5,6,7,8);").SelectScalarInt().First());

                Assert.Equal("foobar", db.Query("SELECT my_concat('foo', 'bar');").SelectScalarString().First());
                Assert.Equal("abc", db.Query("SELECT my_concat('a', 'b', 'c');").SelectScalarString().First());

                {
                    var result = db.Query("SELECT my_mean(1,2,3,4,5,6,7,8);").SelectScalarDouble().First();
                    Assert.True(result >= (36 / 8));
                    Assert.True(result <= (36 / 8 + 1));
                }

                {
                    int val = 5;
                    foreach (var row in db.Query("SELECT makeblob(?);", val))
                    {
                        Assert.Equal(val, row[0].ToBlob().Length);
                    }
                }

                {
                    int val = 5;
                    var c = db.Query("SELECT cube(?);", val).SelectScalarInt64().First();
                    Assert.Equal(val * val * val, c);
                }

                // Test all the extension methods.
                {
                    var result = db.Query("SELECT num_var();").SelectScalarInt().First();
                    Assert.Equal(0, result);

                    result = db.Query("SELECT num_var(1);").SelectScalarInt().First();
                    Assert.Equal(1, result);

                    result = db.Query("SELECT num_var(1, 2);").SelectScalarInt().First();
                    Assert.Equal(2, result);

                    result = db.Query("SELECT num_var(1, 2, 3);").SelectScalarInt().First();
                    Assert.Equal(3, result);

                    result = db.Query("SELECT num_var(1, 2, 3, 4);").SelectScalarInt().First();
                    Assert.Equal(4, result);

                    result = db.Query("SELECT num_var(1, 2, 3, 4, 5);").SelectScalarInt().First();
                    Assert.Equal(5, result);

                    result = db.Query("SELECT num_var(1, 2, 3, 4, 5, 6);").SelectScalarInt().First();
                    Assert.Equal(6, result);

                    result = db.Query("SELECT num_var(1, 2, 3, 4, 5, 6, 7);").SelectScalarInt().First();
                    Assert.Equal(7, result);

                    result = db.Query("SELECT num_var(1, 2, 3, 4, 5, 6, 7, 8);").SelectScalarInt().First();
                    Assert.Equal(8, result);
                }

                {
                    int length = 10;
                    var result = db.Query("SELECT zeroblob(?);", length).Select(rs => rs[0].Length).First();
                    Assert.Equal(length, result);
                }

                {
                    var result = db.Query("SELECT nullFunc();").Select(rs => rs[0].SQLiteType).First();
                    Assert.Equal(SQLiteType.Null, result);
                }
            }
        }

        [Fact]
        public void TestWalCheckpoint()
        {
            var tmpFile = GetTempFile();
            using (var db = SQLite3.Open(tmpFile))
            {
                db.Execute("PRAGMA journal_mode=WAL;");

                // CREATE TABLE results in 2 frames check pointed and increaseses the log size by 2
                // so manually do a checkpoint to reset the counters thus testing both
                // sqlite3_wal_checkpoint and sqlite3_wal_checkpoint_v2.
                db.Execute("CREATE TABLE foo (x int);");
                db.WalCheckPoint("main");

                db.Execute("INSERT INTO foo (x) VALUES (1);");
                db.Execute("INSERT INTO foo (x) VALUES (2);");

                db.WalCheckPoint("main", WalCheckPointMode.Full, out int logSize, out int framesCheckPointed);

                Assert.Equal(2, logSize);
                Assert.Equal(2, framesCheckPointed);
            }

            // Set autocheckpoint to 1 so that regardless of the number of
            // commits, explicit checkpoints only checkpoint the last update.
            var builder = SQLiteDatabaseConnectionBuilder.Create(
                tmpFile,
                autoCheckPointCount: 1);

            using (var db = builder.Build())
            {
                db.Execute("INSERT INTO foo (x) VALUES (3);");
                db.Execute("INSERT INTO foo (x) VALUES (4);");
                db.Execute("INSERT INTO foo (x) VALUES (5);");

                int logSize;
                int framesCheckPointed;
                db.WalCheckPoint("main", WalCheckPointMode.Passive, out logSize, out framesCheckPointed);

                Assert.Equal(1, logSize);
                Assert.Equal(1, framesCheckPointed);
            }

            builder = builder.With(autoCheckPointCount: 0);
            using (var db = builder.Build())
            {
                db.Execute("INSERT INTO foo (x) VALUES (3);");
                db.Execute("INSERT INTO foo (x) VALUES (4);");
                db.Execute("INSERT INTO foo (x) VALUES (5);");

                int logSize;
                int framesCheckPointed;
                db.WalCheckPoint("main", WalCheckPointMode.Passive, out logSize, out framesCheckPointed);

                Assert.True(logSize > 1);
                Assert.True(framesCheckPointed > 1);
            }

            raw.sqlite3__vfs__delete(null, tmpFile, 1);
        }

        [Fact]
        public void TestTableColumnMetadata()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                // out string dataType, out string collSeq, out int notNull, out int primaryKey, out int autoInc
                db.Execute("CREATE TABLE foo (rowid integer primary key asc autoincrement, x int not null);");

                var metadata = db.GetTableColumnMetadata("main", "foo", "x");
                Assert.Equal("INT", metadata.DeclaredType);
                Assert.Equal("BINARY", metadata.CollationSequence);
                Assert.True(metadata.HasNotNullConstraint);
                Assert.False(metadata.IsPrimaryKeyPart);
                Assert.False(metadata.IsAutoIncrement);

                metadata = db.GetTableColumnMetadata("main", "foo", "rowid");
                Assert.Equal("INTEGER", metadata.DeclaredType);
                Assert.Equal("BINARY", metadata.CollationSequence);
                Assert.False(metadata.HasNotNullConstraint);
                Assert.True(metadata.IsPrimaryKeyPart);
                Assert.True(metadata.IsAutoIncrement);
            }
        }

        [Fact]
        public void TestProgressHandler()
        {
            int count = 0;
            var builder = SQLiteDatabaseConnectionBuilder.InMemory.With(
                progressHandler: () =>
                    {
                        count++;
                        return false;
                    },
                progressHandlerInterval: 1);

            using (var db = builder.Build())
            {
                using (var stmt = db.PrepareStatement("SELECT 1;"))
                {
                    stmt.MoveNext();
                }
                Assert.True(count > 0);
            }

            builder = builder.With(progressHandler: () => true);
            using (var db = builder.Build())
            {
                using (var stmt = db.PrepareStatement("SELECT 1;"))
                {
                    Assert.Throws<OperationCanceledException>(() => stmt.MoveNext());
                }
            }

            // Test that assigning null to the handler removes the progress handler.
            builder = builder.Without(progressHandler: true);
            using (var db = builder.Build())
            {
                using (var stmt = db.PrepareStatement("SELECT 1;"))
                {
                    stmt.MoveNext();
                }
            }
        }

        [Fact]
        public void TestAuthorizer()
        {
            var tmpFile = GetTempFile();
            var builder = SQLiteDatabaseConnectionBuilder.Create(tmpFile,
                authorizer: (actionCode, p0, p1, dbName, triggerOrView) =>
                    {
                        switch (actionCode)
                        {
                        // When creating a table an insert is first done.
                            case ActionCode.Insert:
                                Assert.Equal("sqlite_master", p0);
                                Assert.Null(p1);
                                Assert.Equal("main", dbName);
                                Assert.Null(triggerOrView);
                                break;
                            case ActionCode.CreateTable:
                                Assert.Equal("foo", p0);
                                Assert.Null(p1);
                                Assert.Equal("main", dbName);
                                Assert.Null(triggerOrView);
                                break;
                            case ActionCode.Read:
                                Assert.NotNull(p0);
                                Assert.NotNull(p1);
                                Assert.Equal("main", dbName);
                                Assert.Null(triggerOrView);
                                break;
                        }

                        return AuthorizerReturnCode.Ok;
                    });

            using (var db = builder.Build())
            {
                db.ExecuteAll(
                    @"CREATE TABLE foo (x int);
                      SELECT * FROM foo;
                      CREATE VIEW TEST_VIEW AS SELECT * FROM foo;");
            }


            // View authorizer
            builder = builder.With(authorizer: (actionCode, p0, p1, dbName, triggerOrView) =>
                {
                    switch (actionCode)
                    {
                        case ActionCode.Read:
                            Assert.NotNull(p0);
                            Assert.NotNull(p1);
                            Assert.Equal("main", dbName);

                            // A Hack. Goal is to prove that inner_most_trigger_or_view is not null when it is returned in the callback
                            if (p0 == "foo") { Assert.NotNull(triggerOrView); }
                            break;
                    }

                    return AuthorizerReturnCode.Ok;
                });

            using (var db = builder.Build())
            {
                db.Execute("SELECT * FROM TEST_VIEW;");
            }


            // Denied authorizer
            builder = builder.With(authorizer: (actionCode, p0, p1, dbName, triggerOrView) => AuthorizerReturnCode.Deny);
            using (var db = builder.Build())
            {
                try
                {
                    db.Execute("SELECT * FROM TEST_VIEW;");
                    Assert.True(false);
                }
                catch (SQLiteException e)
                {
                    Assert.Equal(ErrorCode.NotAuthorized, e.ErrorCode);
                }
            }

            builder = builder.Without(authorizer: true);
            using (var db = builder.Build())
            {
                db.Execute("SELECT * FROM TEST_VIEW;");
            }

            raw.sqlite3__vfs__delete(null, tmpFile, 1);
        }

        [Fact]
        public void TestStatus()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                db.Status(DatabaseConnectionStatusCode.CacheUsed, out int current, out int highwater, false);

                Assert.True(current > 0);
                Assert.Equal(0, highwater);
            }
        }

        [Fact]
        public void TestVacuum()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                // VACUUM can't be called during a transaction so its a good negative confirmation test.
                Assert.Throws<SQLiteException>(() => db.RunInTransaction(tdb => tdb.Vacuum()));
                db.Vacuum();
            }
        }

        [Fact]
        public void TestTransaction()
        {
            using (var db = SQLite3.OpenInMemory())
            {
                var result = db.RunInTransaction(tdb =>
                    {
                        tdb.RunInTransaction(_tdb => _tdb.Execute("CREATE TABLE foo (x int);"));
                        tdb.TryRunInTransaction(_tdb =>
                            {
                                _tdb.Execute("INSERT INTO foo (x) VALUES (1);");
                                _tdb.Execute("INSERT INTO foo (x) VALUES (2);");
                                _tdb.Execute("INSERT INTO foo (x) VALUES (3);");
                            });
                        Assert.Equal(new int[] { 1, 2, 3 }, tdb.Query("SELECT * FROM foo").SelectScalarInt().ToList());

                        var failedResult = tdb.TryRunInTransaction(_tdb =>
                            {
                                _tdb.Execute("INSERT INTO foo (x) VALUES (1);");
                                _tdb.Execute("INSERT INTO foo (x) VALUES (2);");
                                _tdb.Execute("INSERT INTO foo (x) VALUES (3);");
                                throw new Exception();
                            });
                        Assert.False(failedResult);
                        Assert.Equal(new int[] { 1, 2, 3 }, tdb.Query("SELECT * FROM foo").SelectScalarInt().ToList());

                        string successResult;
                        if(tdb.TryRunInTransaction(_tdb =>
                            {
                                _tdb.Execute("INSERT INTO foo (x) VALUES (1);");
                                _tdb.Execute("INSERT INTO foo (x) VALUES (2);");
                                _tdb.Execute("INSERT INTO foo (x) VALUES (3);");
                                return "SUCCESS";
                            }, out successResult))
                        {
                            Assert.Equal("SUCCESS", successResult);
                        }
                        else
                        {
                            Assert.True(false, "expect the transaction to succeed");
                        }

                        return "SUCCESS";
                    });

                Assert.Equal("SUCCESS", result);

                db.RunInTransaction(_ => {}, TransactionMode.Exclusive);
                db.RunInTransaction(_ => {}, TransactionMode.Immediate);
                Assert.Throws<ArgumentException>(() => db.RunInTransaction(_ => {}, (TransactionMode) int.MaxValue));
            }
        }
    }
}
