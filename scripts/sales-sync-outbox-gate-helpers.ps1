# Shared structural helpers for the F4 outbox gates.  They deliberately avoid
# "next member" slicing: C# method boundaries are found from balanced braces or
# the terminating semicolon of an expression-bodied member.  The lexer covers
# the C# 8 string/comment forms used by the net48 source tree.

function Get-CSharpTriviaEnd(
    [string]$text,
    [int]$index) {
    $length = $text.Length
    if ($index -ge $length) {
        return $index
    }

    if ($text[$index] -eq '/' -and $index + 1 -lt $length) {
        if ($text[$index + 1] -eq '/') {
            $cursor = $index + 2
            while ($cursor -lt $length -and $text[$cursor] -ne "`n") {
                $cursor++
            }
            return $cursor
        }

        if ($text[$index + 1] -eq '*') {
            $cursor = $index + 2
            while ($cursor + 1 -lt $length) {
                if ($text[$cursor] -eq '*' -and $text[$cursor + 1] -eq '/') {
                    return $cursor + 2
                }
                $cursor++
            }
            return $length
        }
    }

    if ($text[$index] -eq "'") {
        $cursor = $index + 1
        while ($cursor -lt $length) {
            if ($text[$cursor] -eq '\') {
                $cursor += 2
                continue
            }
            if ($text[$cursor] -eq "'") {
                return $cursor + 1
            }
            $cursor++
        }
        return $length
    }

    $quoteStart = -1
    $isVerbatim = $false
    if ($text[$index] -eq '"') {
        $quoteStart = $index
    } elseif ($text[$index] -eq '@' -and $index + 1 -lt $length -and $text[$index + 1] -eq '"') {
        $quoteStart = $index + 1
        $isVerbatim = $true
    } elseif ($text[$index] -eq '$' -and $index + 1 -lt $length -and $text[$index + 1] -eq '"') {
        $quoteStart = $index + 1
    } elseif ($text[$index] -eq '$' -and $index + 2 -lt $length -and
        $text[$index + 1] -eq '@' -and $text[$index + 2] -eq '"') {
        $quoteStart = $index + 2
        $isVerbatim = $true
    } elseif ($text[$index] -eq '@' -and $index + 2 -lt $length -and
        $text[$index + 1] -eq '$' -and $text[$index + 2] -eq '"') {
        $quoteStart = $index + 2
        $isVerbatim = $true
    }

    if ($quoteStart -lt 0) {
        return $index
    }

    $cursor = $quoteStart + 1
    while ($cursor -lt $length) {
        if (-not $isVerbatim -and $text[$cursor] -eq '\') {
            $cursor += 2
            continue
        }
        if ($text[$cursor] -eq '"') {
            if ($isVerbatim -and $cursor + 1 -lt $length -and $text[$cursor + 1] -eq '"') {
                $cursor += 2
                continue
            }
            return $cursor + 1
        }
        $cursor++
    }

    return $length
}

function Find-CSharpMatchingDelimiter(
    [string]$text,
    [int]$openIndex,
    [char]$openCharacter,
    [char]$closeCharacter) {
    if ($openIndex -lt 0 -or $openIndex -ge $text.Length -or $text[$openIndex] -ne $openCharacter) {
        return -1
    }

    $depth = 0
    $cursor = $openIndex
    while ($cursor -lt $text.Length) {
        $triviaEnd = Get-CSharpTriviaEnd $text $cursor
        if ($triviaEnd -ne $cursor) {
            $cursor = $triviaEnd
            continue
        }

        if ($text[$cursor] -eq $openCharacter) {
            $depth++
        } elseif ($text[$cursor] -eq $closeCharacter) {
            $depth--
            if ($depth -eq 0) {
                return $cursor
            }
        }
        $cursor++
    }

    return -1
}

function Find-CSharpMethodBodyStart(
    [string]$text,
    [int]$declarationStart) {
    $seenParameterList = $false
    $parameterDepth = 0
    $cursor = $declarationStart
    while ($cursor -lt $text.Length) {
        $triviaEnd = Get-CSharpTriviaEnd $text $cursor
        if ($triviaEnd -ne $cursor) {
            $cursor = $triviaEnd
            continue
        }

        $character = $text[$cursor]
        if ($character -eq '(') {
            $seenParameterList = $true
            $parameterDepth++
        } elseif ($character -eq ')' -and $parameterDepth -gt 0) {
            $parameterDepth--
        } elseif ($seenParameterList -and $parameterDepth -eq 0) {
            if ($character -eq '{') {
                return [pscustomobject]@{ Kind = "block"; Index = $cursor }
            }
            if ($character -eq '=' -and $cursor + 1 -lt $text.Length -and $text[$cursor + 1] -eq '>') {
                return [pscustomobject]@{ Kind = "expression"; Index = $cursor }
            }
            if ($character -eq ';') {
                return $null
            }
        }
        $cursor++
    }

    return $null
}

function Find-CSharpExpressionEnd(
    [string]$text,
    [int]$expressionArrowIndex) {
    $cursor = $expressionArrowIndex + 2
    while ($cursor -lt $text.Length) {
        $triviaEnd = Get-CSharpTriviaEnd $text $cursor
        if ($triviaEnd -ne $cursor) {
            $cursor = $triviaEnd
            continue
        }

        if ($text[$cursor] -eq ';') {
            return $cursor
        }
        $cursor++
    }

    return -1
}

function Get-CSharpMethodSlices(
    [string]$text,
    [string]$access,
    [string]$methodName) {
    $pattern = "(?m)^\s*" + [regex]::Escape($access) +
        "\s+(?:static\s+)?(?:async\s+)?Task(?:<[^\r\n]+>)?\s+" +
        [regex]::Escape($methodName) + "\s*\("
    $matches = [regex]::Matches($text, $pattern)
    $slices = New-Object System.Collections.Generic.List[object]
    foreach ($match in $matches) {
        $body = Find-CSharpMethodBodyStart $text $match.Index
        if ($null -eq $body) {
            continue
        }

        if ($body.Kind -eq "block") {
            $end = Find-CSharpMatchingDelimiter $text $body.Index '{' '}'
        } else {
            $end = Find-CSharpExpressionEnd $text $body.Index
        }
        if ($end -lt $body.Index) {
            continue
        }

        $slices.Add([pscustomobject]@{
                Start = $match.Index
                End = $end
                BodyKind = $body.Kind
                Text = $text.Substring($match.Index, $end - $match.Index + 1)
            }) | Out-Null
    }

    return $slices.ToArray()
}

function Get-CSharpTestMethodSlices(
    [string]$text,
    [string]$methodName) {
    $slices = @(Get-CSharpMethodSlices $text "public" $methodName)
    return @($slices | Where-Object {
        $prefixStart = [Math]::Max(0, $_.Start - 1024)
        $prefix = $text.Substring($prefixStart, $_.Start - $prefixStart)
        $prefix -match "(?s)\[TestMethod\]\s*$"
    })
}

function Get-CSharpDapperAsyncInvocations([string]$text) {
    $pattern = "\bconn\s*\.\s*(?<method>(?:Execute(?:Scalar)?|Query(?:Single(?:OrDefault)?)?)Async)(?:<[^>]+>)?\s*\("
    $matches = [regex]::Matches($text, $pattern)
    $invocations = New-Object System.Collections.Generic.List[object]
    foreach ($match in $matches) {
        $openIndex = $match.Index + $match.Length - 1
        $callEnd = Find-CSharpMatchingDelimiter $text $openIndex '(' ')'
        if ($callEnd -lt $openIndex) {
            continue
        }

        $end = $callEnd
        $cursor = $callEnd + 1
        while ($cursor -lt $text.Length -and [char]::IsWhiteSpace($text[$cursor])) {
            $cursor++
        }
        $configure = [regex]::Match(
            $text.Substring($cursor),
            "^\.\s*ConfigureAwait\s*\(")
        if ($configure.Success) {
            $configureOpen = $cursor + $configure.Length - 1
            $configureEnd = Find-CSharpMatchingDelimiter $text $configureOpen '(' ')'
            if ($configureEnd -ge $configureOpen) {
                $end = $configureEnd
            }
        }

        $invocations.Add([pscustomobject]@{
                Start = $match.Index
                End = $end
                Method = $match.Groups["method"].Value
                Text = $text.Substring($match.Index, $end - $match.Index + 1)
            }) | Out-Null
    }

    return $invocations.ToArray()
}

function Get-CSharpPreviousStructuralSemicolon(
    [string]$text,
    [int]$beforeIndex) {
    $last = -1
    $cursor = 0
    while ($cursor -lt $beforeIndex) {
        $triviaEnd = Get-CSharpTriviaEnd $text $cursor
        if ($triviaEnd -ne $cursor) {
            $cursor = $triviaEnd
            continue
        }

        if ($text[$cursor] -eq ';') {
            $last = $cursor
        }
        $cursor++
    }

    return $last
}

function Test-CSharpInvocationUsesTransaction(
    [string]$invocationText,
    [string]$transactionName = "tx") {
    $transaction = [regex]::Escape($transactionName)
    return $invocationText -match "(?s)(?:,\s*$transaction|transaction\s*:\s*$transaction)\s*\)\s*(?:\.\s*ConfigureAwait\s*\(\s*false\s*\))?$"
}
