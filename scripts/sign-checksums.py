"""Signs a release's SHA256SUMS with the release signing key, and checks such a signature, with the openssl command line.

sign <SHA256SUMS> <SHA256SUMS.sig>: the private key (PKCS #8 PEM, ECDSA P-256) comes from the environment variable RELEASE_SIGNING_KEY,
never from a file in the checkout or the command line.
verify <SHA256SUMS> <SHA256SUMS.sig> <public key>: the public key is the Base64 of X and Y, as install.ps1 pins it in $taskReleaseKey.

The signature file is the Base64 of r and s, 32 bytes each, and a line feed: the IEEE P1363 form that .NET's ECDsa.VerifyData reads in
Windows PowerShell 5.1 and in the app, not the DER form openssl writes.
"""
import base64
import os
import subprocess
import sys
import tempfile
from pathlib import Path

# SubjectPublicKeyInfo of an uncompressed P-256 point, up to the point itself: id-ecPublicKey, prime256v1, then the 0x04 prefix.
P256_PUBLIC_KEY_PREFIX = bytes.fromhex('3059301306072a8648ce3d020106082a8648ce3d030107034200') + b'\x04'


def der_length(data, offset):
    first = data[offset]
    if first < 0x80:
        return first, offset + 1
    count = first & 0x7f
    if count not in (1, 2) or offset + 1 + count > len(data):
        raise ValueError('The signature has a malformed length.')
    return int.from_bytes(data[offset + 1:offset + 1 + count], 'big'), offset + 1 + count


def der_to_raw(signature):
    """r and s of a DER ECDSA signature (SEQUENCE of two INTEGERs), 32 bytes each."""
    if len(signature) < 8 or signature[0] != 0x30:
        raise ValueError('The signature is not a DER sequence.')
    length, offset = der_length(signature, 1)
    if offset + length != len(signature):
        raise ValueError('The signature has bytes outside its sequence.')
    parts = []
    for _ in range(2):
        if offset >= len(signature) or signature[offset] != 0x02:
            raise ValueError('The signature does not hold two integers.')
        length, offset = der_length(signature, offset + 1)
        value = signature[offset:offset + length].lstrip(b'\x00')
        if length == 0 or offset + length > len(signature) or len(value) > 32:
            raise ValueError('A signature integer is malformed or too long for P-256.')
        parts.append(value.rjust(32, b'\x00'))
        offset += length
    if offset != len(signature):
        raise ValueError('The signature has bytes after its integers.')
    return parts[0] + parts[1]


def raw_to_der(raw):
    """The DER form of r and s, 32 bytes each, for openssl."""
    if len(raw) != 64:
        raise ValueError('The signature is not 64 bytes.')

    def integer(value):
        value = value.lstrip(b'\x00') or b'\x00'
        if value[0] & 0x80:
            value = b'\x00' + value
        return b'\x02' + bytes([len(value)]) + value

    body = integer(raw[:32]) + integer(raw[32:])
    return b'\x30' + bytes([len(body)]) + body


def openssl(*arguments):
    return subprocess.run(['openssl', *arguments], check=False, capture_output=True)


def sign(checksums, signature_path, private_key_pem):
    with tempfile.TemporaryDirectory() as folder:
        key_path = Path(folder, 'key.pem')
        descriptor = os.open(key_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, 'w', encoding='ascii') as key_file:
            key_file.write(private_key_pem)
        result = openssl('dgst', '-sha256', '-sign', str(key_path), str(checksums))
        if result.returncode != 0:
            raise ValueError('openssl could not sign: ' + result.stderr.decode(errors='replace').strip())
    Path(signature_path).write_text(base64.b64encode(der_to_raw(result.stdout)).decode('ascii') + '\n', encoding='ascii', newline='\n')


def verify(checksums, signature_path, public_key):
    point = base64.b64decode(public_key, validate=True)
    raw = base64.b64decode(Path(signature_path).read_text(encoding='ascii').strip(), validate=True)
    if len(point) != 64:
        raise ValueError('The public key is not the 64 bytes of X and Y.')
    with tempfile.TemporaryDirectory() as folder:
        key_path, signature_der = Path(folder, 'public.der'), Path(folder, 'signature.der')
        key_path.write_bytes(P256_PUBLIC_KEY_PREFIX + point)
        signature_der.write_bytes(raw_to_der(raw))
        result = openssl('dgst', '-sha256', '-verify', str(key_path), '-keyform', 'DER', '-signature', str(signature_der), str(checksums))
    return result.returncode == 0


def main(arguments):
    command, parameters = arguments[0], arguments[1:]
    if command == 'sign':
        key = os.environ.get('RELEASE_SIGNING_KEY', '')
        if 'PRIVATE KEY' not in key:
            raise ValueError('RELEASE_SIGNING_KEY holds no private key; the release environment must provide it.')
        sign(parameters[0], parameters[1], key)
        print(f'Signed {parameters[0]}.')
    elif command == 'verify':
        if not verify(parameters[0], parameters[1], parameters[2]):
            raise ValueError(f'{parameters[1]} is not a valid signature of {parameters[0]} by this public key.')
        print(f'{parameters[1]} is a valid signature of {parameters[0]}.')
    else:
        raise ValueError(f'Unknown command {command!r}.')


if __name__ == '__main__':
    try:
        main(sys.argv[1:])
    except (ValueError, IndexError, OSError) as error:
        print(f'::error::{error}', file=sys.stderr)
        sys.exit(1)
