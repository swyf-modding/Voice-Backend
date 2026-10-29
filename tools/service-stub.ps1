# A stand-in for the speech services, so the remote paths can be exercised without an account.
#
#   powershell -ExecutionPolicy Bypass -File tools/service-stub.ps1 -Port 8099
#
# Then point the mod at it from BepInEx/config/com.community.scamwyf.voicebackend.cfg:
#
#   [2 - Backend]
#   AsrService = Gladia          # or Deepgram, or OpenAi
#   TtsService = ElevenLabs      # or Deepgram, or OpenAi
#
#   [2 - Remote API]
#   GladiaUrl = http://127.0.0.1:8099       (same for DeepgramUrl, ElevenLabsUrl, OpenAiUrl)
#   GladiaApiKey = test                      (any non-empty value)
#
# and start the game with VOICEBACKEND_SELFTEST=1, which synthesises a sentence and transcribes it
# back. The stub answers with the payload shapes each service documents, and appends one line per
# request to service-stub.log: method, path, the authorization header, the content type, and whether
# a multipart body carried a file part. That is enough to see that the header names, the multipart
# encoding and the Gladia polling actually happen, which no unit test can show.
#
# It is a development tool: it does not validate keys, and it returns the same short tone for every
# synthesis request.

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "stub listening on $Port"

function Respond([System.Net.HttpListenerContext]$ctx, [int]$status, [string]$type, [byte[]]$body) {
    $ctx.Response.StatusCode = $status
    $ctx.Response.ContentType = $type
    if ($body -and $body.Length -gt 0) {
        $ctx.Response.OutputStream.Write($body, 0, $body.Length)
    }
    $ctx.Response.Close()
}

function Tone([int]$milliseconds, [int]$rate) {
    $count = [int]($rate * $milliseconds / 1000)
    $bytes = New-Object byte[] ($count * 2)
    for ($i = 0; $i -lt $count; $i++) {
        $value = [int16](8000 * [Math]::Sin(2 * [Math]::PI * 220 * $i / $rate))
        $bytes[$i * 2] = [byte]($value -band 0xFF)
        $bytes[$i * 2 + 1] = [byte](($value -shr 8) -band 0xFF)
    }
    return $bytes
}

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $path = $ctx.Request.Url.AbsolutePath
    $method = $ctx.Request.HttpMethod

    $reader = New-Object System.IO.StreamReader($ctx.Request.InputStream)
    $body = $reader.ReadToEnd()
    $auth = $ctx.Request.Headers["Authorization"]
    if (-not $auth) { $auth = $ctx.Request.Headers["xi-api-key"] }
    if (-not $auth) { $auth = $ctx.Request.Headers["x-gladia-key"] }

    $note = "$method $path auth=$auth type=$($ctx.Request.ContentType) bytes=$($ctx.Request.ContentLength)"
    if ($body -like "*filename=*") { $note += " multipart=yes" }
    if ($body -like "*utterance.wav*") { $note += " file=utterance.wav" }
    Add-Content -Path ".work-stub.log" -Value $note
    Write-Host $note

    switch -Regex ($path) {
        "^/v1/text-to-speech/" { Respond $ctx 200 "audio/pcm" (Tone 500 24000); break }
        "^/v1/speak"           { Respond $ctx 200 "audio/wav" (Tone 500 24000); break }
        "^/v1/audio/speech"    { Respond $ctx 200 "audio/pcm" (Tone 500 24000); break }
        "^/v1/listen" {
            $json = '{"results":{"channels":[{"alternatives":[{"transcript":"stub deepgram heard this","confidence":0.91}]}]}}'
            Respond $ctx 200 "application/json" ([Text.Encoding]::UTF8.GetBytes($json)); break
        }
        "^/v1/audio/transcriptions" {
            Respond $ctx 200 "application/json" ([Text.Encoding]::UTF8.GetBytes('{"text":"stub openai heard this"}')); break
        }
        "^/v2/upload" {
            Respond $ctx 200 "application/json" ([Text.Encoding]::UTF8.GetBytes('{"file_url":"http://127.0.0.1:8099/audio/upload.wav"}')); break
        }
        "^/v2/pre-recorded$" {
            Respond $ctx 200 "application/json" ([Text.Encoding]::UTF8.GetBytes('{"id":"job-1","status":"queued"}')); break
        }
        "^/v2/pre-recorded/job-1" {
            $json = '{"id":"job-1","status":"done","result":{"transcription":{"utterances":[{"text":"stub gladia heard this"}]}}}'
            Respond $ctx 200 "application/json" ([Text.Encoding]::UTF8.GetBytes($json)); break
        }
        default { Respond $ctx 404 "text/plain" ([Text.Encoding]::UTF8.GetBytes("no stub for $path")) }
    }
}
