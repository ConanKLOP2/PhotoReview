# A/A split generator for tune-rank.ps1 (dot-sourced). Own file so tests can dot-source it without running the ranker.
# A split is a bool[] of length N: $true = run goes to group A. No integer bitmask, so it is correct for any N (a 32-bit
# `1 -shl $i` wraps at i >= 32 and silently pairs run i with run i-32).

function New-AaSplits([int]$N, [System.Random]$Rng, [int]$RandomSplits = 100) {
    $half = [int][math]::Floor($N / 2)
    $splits = New-Object System.Collections.Generic.List[object]
    if ($N -le 10) {
        # exhaustive: every way to put $half of the runs in A
        for ($mask = 1; $mask -lt (1 -shl $N); $mask++) {
            $bits = 0; for ($i = 0; $i -lt $N; $i++) { if ($mask -band (1 -shl $i)) { $bits++ } }
            if ($bits -ne $half) { continue }
            if ($N % 2 -eq 0 -and -not ($mask -band 1)) { continue }   # even N: A and B are mirror images; keep one of each pair
            $inA = New-Object 'bool[]' $N
            for ($i = 0; $i -lt $N; $i++) { $inA[$i] = [bool]($mask -band (1 -shl $i)) }
            $splits.Add($inA)
        }
    }
    else {
        for ($s = 0; $s -lt $RandomSplits; $s++) {
            $perm = @(0..($N - 1) | Sort-Object { $Rng.Next() })
            $inA = New-Object 'bool[]' $N
            foreach ($i in $perm[0..($half - 1)]) { $inA[$i] = $true }
            $splits.Add($inA)
        }
    }
    , $splits
}
