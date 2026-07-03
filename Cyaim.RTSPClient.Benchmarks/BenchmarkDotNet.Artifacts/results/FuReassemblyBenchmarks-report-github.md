```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8655)
12th Gen Intel Core i5-12400, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2
  DefaultJob : .NET 10.0.9 (10.0.926.27113), X64 RyuJIT AVX2


```
| Method               | Mean     | Error    | StdDev   | Gen0    | Gen1    | Gen2    | Allocated |
|--------------------- |---------:|---------:|---------:|--------:|--------:|--------:|----------:|
| Reassemble_100KB_Idr | 55.50 μs | 1.417 μs | 4.156 μs | 32.2266 | 32.2266 | 32.2266 | 116.12 KB |
