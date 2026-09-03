import socket
print("Listening on UDP 5000...")
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind(("0.0.0.0", 5000))
sock.settimeout(10)
try:
    data, addr = sock.recvfrom(65535)
    print(f"SUCCESS - got {len(data)} bytes from {addr}")
    print(f'Got {len(data)} bytes, first byte: {hex(data[0])}')
except socket.timeout:
    print("No packets received")
finally:
    sock.close()