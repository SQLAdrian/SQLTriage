# Extract string literals from C#, Razor, JS, and SQL files

function Extract-AllStrings {
    param([string[]]$Paths)

    $results = @()

    foreach ($file in $Paths) {
        try {
            $content = Get-Content $file -Raw -ErrorAction SilentlyContinue
            if (-not $content) { continue }

            # C# / Razor: double-quoted strings "..." (not empty strings)
            $content -match '""\s*[+&]|\s*[+&]\s*""' | Out-Null  # Skip concatenation places
            $csDoubleRegex = [regex]::new('"((?:[^"\\]|\\.|[^"]){25,})"', 'Singleline')
            foreach ($m in $csDoubleRegex.Matches($content)) {
                $str = $m.Groups[1].Value
                if ($str.Length -ge 25) {
                    $results += [PSCustomObject]@{ Text=$str; File=$file }
                }
            }

            # C# verbatim strings @"..."
            $csVerbatimRegex = [regex]::new('@\"((?:[^\"]|\"{1,2}){25,})\"', 'Singleline')
            foreach ($m in $csVerbatimRegex.Matches($content)) {
                $str = $m.Groups[1].Value
                if ($str.Length -ge 25) {
                    $results += [PSCustomObject]@{ Text=$str; File=$file }
                }
            }

            # JavaScript: single '...' and double "..." and backtick `...`
            # Filter out minified / property key patterns
            $jsSingleRegex = [regex]::new("'((?:[^'\\]|\\.){25,})'", 'Singleline')
            foreach ($m in $jsSingleRegex.Matches($content)) {
                $str = $m.Groups[1].Value
                if ($str.Length -ge 25) {
                    $results += [PSCustomObject]@{ Text=$str; File=$file }
                }
            }

            $jsDoubleRegex = [regex]::new('"((?:[^"\\]|\\.){25,})"', 'Singleline')
            foreach ($m in $jsDoubleRegex.Matches($content)) {
                $str = $m.Groups[1].Value
                if ($str.Length -ge 25) {
                    $results += [PSCustomObject]@{ Text=$str; File=$file }
                }
            }

            $jsBacktickRegex = [regex]::new('\`((?:[^\`\\]|\\.|\\`\`){25,})\`', 'Singleline')
            foreach ($m in $jsBacktickRegex.Matches($content)) {
                $str = $m.Groups[1].Value
                if ($str.Length -ge 25) {
                    $results += [PSCustomObject]@{ Text=$str; File=$file }
                }
            }

            # SQL: single-quoted strings
            $sqlRegex = [regex]::new("'((?:[^'\\]|\\.){25,})'", 'Singleline')
            foreach ($m in $sqlRegex.Matches($content)) {
                $str = $m.Groups[1].Value
                if ($str.Length -ge 25) {
                    $results += [PSCustomObject]@{ Text=$str; File=$file }
                }
            }

        } catch { }
    }

    return $results
}

# Get all relevant files
\$csFiles = Get-ChildItem -Recurse -File | Where-Object { \$_.Extension -in '.cs', '.razor' }
\$jsFiles = Get-ChildItem -Recurse -File | Where-Object { \$_.Extension -eq '.js' }
\$sqlFiles = Get-ChildItem -Recurse -File | Where-Object { \$_.Extension -eq '.sql' }

\$allFiles = @(\$csFiles) + @(\$jsFiles) + @(\$sqlFiles)

\$extracted = Extract-AllStrings -Paths \$allFiles

# Filter: at least 8 words split by whitespace
\$minWords = 8
\$filtered = foreach (\$item in \$extracted) {
    \$wordCount = (\$item.Text -split '\s+').Where{ \$_ -ne '' }.Count
    if (\$wordCount -ge \$minWords) {
        [PSCustomObject]@{
            Text = \$item.Text
            CharCount = \$item.Text.Length
            File = \$item.File
        }
    }
}

# Group by text
\$grouped = \$filtered | Group-Object Text | ForEach-Object {
    \$files = \$_.Group | Select-Object -ExpandProperty File -Unique
    [PSCustomObject]@{
        Text = \$_.Name
        CharCount = (\$_.Group | Select-Object -First 1).CharCount
        TotalOccurrences = \$_.Count
        FileCount = \$files.Count
        FilePaths = @((\$files | Select-Object -First 3).FullName)
    }
}

# Sort and output JSON
\$grouped | Sort-Object TotalOccurrences, CharCount -Descending | ConvertTo-Json -Depth 4
