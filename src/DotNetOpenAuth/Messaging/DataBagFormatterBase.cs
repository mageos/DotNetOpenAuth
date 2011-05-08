﻿//-----------------------------------------------------------------------
// <copyright file="DataBagFormatterBase.cs" company="Andrew Arnott">
//     Copyright (c) Andrew Arnott. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace DotNetOpenAuth.Messaging {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.Contracts;
	using System.IO;
	using System.Linq;
	using System.Security.Cryptography;
	using System.Text;
	using System.Web;
	using DotNetOpenAuth.Messaging;
	using DotNetOpenAuth.Messaging.Bindings;
	using DotNetOpenAuth.Messaging.Reflection;

	/// <summary>
	/// A serializer for <see cref="DataBag"/>-derived types
	/// </summary>
	/// <typeparam name="T">The DataBag-derived type that is to be serialized/deserialized.</typeparam>
	internal abstract class DataBagFormatterBase<T> : IDataBagFormatter<T> where T : DataBag, new() {
		/// <summary>
		/// The message description cache to use for data bag types.
		/// </summary>
		protected static readonly MessageDescriptionCollection MessageDescriptions = new MessageDescriptionCollection();

		/// <summary>
		/// The length of the nonce to include in tokens that can be decoded once only.
		/// </summary>
		private const int NonceLength = 6;

		/// <summary>
		/// The symmetric secret used for signing/encryption of verification codes and refresh tokens.
		/// </summary>
		private readonly byte[] symmetricSecret;

		/// <summary>
		/// The hashing algorithm to use while signing when using a symmetric secret.
		/// </summary>
		private readonly HashAlgorithm symmetricHasher;

		/// <summary>
		/// The crypto to use for signing access tokens.
		/// </summary>
		private readonly RSACryptoServiceProvider asymmetricSigning;

		/// <summary>
		/// The crypto to use for encrypting access tokens.
		/// </summary>
		private readonly RSACryptoServiceProvider asymmetricEncrypting;

		/// <summary>
		/// The hashing algorithm to use for asymmetric signatures.
		/// </summary>
		private readonly HashAlgorithm hasherForAsymmetricSigning;

		/// <summary>
		/// A value indicating whether the data in this instance will be protected against tampering.
		/// </summary>
		private readonly bool signed;

		/// <summary>
		/// The nonce store to use to ensure that this instance is only decoded once.
		/// </summary>
		private readonly INonceStore decodeOnceOnly;

		/// <summary>
		/// The maximum age of a token that can be decoded; useful only when <see cref="decodeOnceOnly"/> is <c>true</c>.
		/// </summary>
		private readonly TimeSpan? maximumAge;

		/// <summary>
		/// A value indicating whether the data in this instance will be protected against eavesdropping.
		/// </summary>
		private readonly bool encrypted;

		/// <summary>
		/// A value indicating whether the data in this instance will be GZip'd.
		/// </summary>
		private readonly bool compressed;

		/// <summary>
		/// Initializes a new instance of the <see cref="UriStyleMessageFormatter&lt;T&gt;"/> class.
		/// </summary>
		/// <param name="signingKey">The crypto service provider with the asymmetric key to use for signing or verifying the token.</param>
		/// <param name="encryptingKey">The crypto service provider with the asymmetric key to use for encrypting or decrypting the token.</param>
		/// <param name="compressed">A value indicating whether the data in this instance will be GZip'd.</param>
		/// <param name="maximumAge">The maximum age of a token that can be decoded; useful only when <paramref name="decodeOnceOnly"/> is <c>true</c>.</param>
		/// <param name="decodeOnceOnly">The nonce store to use to ensure that this instance is only decoded once.</param>
		protected DataBagFormatterBase(RSACryptoServiceProvider signingKey = null, RSACryptoServiceProvider encryptingKey = null, bool compressed = false, TimeSpan? maximumAge = null, INonceStore decodeOnceOnly = null)
			: this(signingKey != null, encryptingKey != null, compressed, maximumAge, decodeOnceOnly) {
			this.asymmetricSigning = signingKey;
			this.asymmetricEncrypting = encryptingKey;
			this.hasherForAsymmetricSigning = new SHA1CryptoServiceProvider();
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="UriStyleMessageFormatter&lt;T&gt;"/> class.
		/// </summary>
		/// <param name="symmetricSecret">The symmetric secret to use for signing and encrypting.</param>
		/// <param name="signed">A value indicating whether the data in this instance will be protected against tampering.</param>
		/// <param name="encrypted">A value indicating whether the data in this instance will be protected against eavesdropping.</param>
		/// <param name="compressed">A value indicating whether the data in this instance will be GZip'd.</param>
		/// <param name="maximumAge">The maximum age of a token that can be decoded; useful only when <paramref name="decodeOnceOnly"/> is <c>true</c>.</param>
		/// <param name="decodeOnceOnly">The nonce store to use to ensure that this instance is only decoded once.</param>
		protected DataBagFormatterBase(byte[] symmetricSecret = null, bool signed = false, bool encrypted = false, bool compressed = false, TimeSpan? maximumAge = null, INonceStore decodeOnceOnly = null)
			: this(signed, encrypted, compressed, maximumAge, decodeOnceOnly) {
			Contract.Requires<ArgumentException>(symmetricSecret != null || (!signed && !encrypted), "A secret is required when signing or encrypting is required.");

			if (symmetricSecret != null) {
				this.symmetricHasher = new HMACSHA256(symmetricSecret);
			}

			this.symmetricSecret = symmetricSecret;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="UriStyleMessageFormatter&lt;T&gt;"/> class.
		/// </summary>
		/// <param name="signed">A value indicating whether the data in this instance will be protected against tampering.</param>
		/// <param name="encrypted">A value indicating whether the data in this instance will be protected against eavesdropping.</param>
		/// <param name="compressed">A value indicating whether the data in this instance will be GZip'd.</param>
		/// <param name="maximumAge">The maximum age of a token that can be decoded; useful only when <paramref name="decodeOnceOnly"/> is <c>true</c>.</param>
		/// <param name="decodeOnceOnly">The nonce store to use to ensure that this instance is only decoded once.</param>
		private DataBagFormatterBase(bool signed = false, bool encrypted = false, bool compressed = false, TimeSpan? maximumAge = null, INonceStore decodeOnceOnly = null) {
			Contract.Requires<ArgumentException>(signed || decodeOnceOnly == null, "A signature must be applied if this data is meant to be decoded only once.");
			Contract.Requires<ArgumentException>(maximumAge.HasValue || decodeOnceOnly == null, "A maximum age must be given if a message can only be decoded once.");

			this.signed = signed;
			this.maximumAge = maximumAge;
			this.decodeOnceOnly = decodeOnceOnly;
			this.encrypted = encrypted;
			this.compressed = compressed;
		}

		/// <summary>
		/// Serializes the specified message, including compression, encryption, signing, and nonce handling where applicable.
		/// </summary>
		/// <param name="message">The message to serialize.  Must not be null.</param>
		/// <returns>A non-null, non-empty value.</returns>
		public string Serialize(T message) {
			message.UtcCreationDate = DateTime.UtcNow;

			if (this.decodeOnceOnly != null) {
				message.Nonce = MessagingUtilities.GetNonCryptoRandomData(NonceLength);
			}

			byte[] encoded = this.SerializeCore(message);

			if (this.compressed) {
				encoded = MessagingUtilities.Compress(encoded);
			}

			if (this.encrypted) {
				encoded = this.Encrypt(encoded);
			}

			if (this.signed) {
				message.Signature = this.CalculateSignature(encoded);
			}

			int capacity = this.signed ? 4 + message.Signature.Length + 4 + encoded.Length : encoded.Length;
			var finalStream = new MemoryStream(capacity);
			var writer = new BinaryWriter(finalStream);
			if (this.signed) {
				writer.WriteBuffer(message.Signature);
			}

			writer.WriteBuffer(encoded);
			writer.Flush();

			return Convert.ToBase64String(finalStream.ToArray());
		}

		/// <summary>
		/// Deserializes a <see cref="DataBag"/>, including decompression, decryption, signature and nonce validation where applicable.
		/// </summary>
		/// <param name="containingMessage">The message that contains the <see cref="DataBag"/> serialized value.  Must not be nulll.</param>
		/// <param name="value">The serialized form of the <see cref="DataBag"/> to deserialize.  Must not be null or empty.</param>
		/// <returns>The deserialized value.  Never null.</returns>
		public T Deserialize(IProtocolMessage containingMessage, string value) {
			var message = new T { ContainingMessage = containingMessage };
			byte[] data = Convert.FromBase64String(value);

			byte[] signature = null;
			if (this.signed) {
				var dataStream = new MemoryStream(data);
				var dataReader = new BinaryReader(dataStream);
				signature = dataReader.ReadBuffer();
				data = dataReader.ReadBuffer();

				// Verify that the verification code was issued by message authorization server.
				ErrorUtilities.VerifyProtocol(this.IsSignatureValid(data, signature), MessagingStrings.SignatureInvalid);
			}

			if (this.encrypted) {
				data = this.Decrypt(data);
			}

			if (this.compressed) {
				data = MessagingUtilities.Decompress(data);
			}

			this.DeserializeCore(message, data);
			message.Signature = signature; // TODO: we don't really need this any more, do we?

			if (this.maximumAge.HasValue) {
				// Has message verification code expired?
				DateTime expirationDate = message.UtcCreationDate + this.maximumAge.Value;
				if (expirationDate < DateTime.UtcNow) {
					throw new ExpiredMessageException(expirationDate, containingMessage);
				}
			}

			// Has message verification code already been used to obtain an access/refresh token?
			if (this.decodeOnceOnly != null) {
				ErrorUtilities.VerifyInternal(this.maximumAge.HasValue, "Oops!  How can we validate a nonce without a maximum message age?");
				string context = "{" + GetType().FullName + "}";
				if (!this.decodeOnceOnly.StoreNonce(context, Convert.ToBase64String(message.Nonce), message.UtcCreationDate)) {
					Logger.OpenId.ErrorFormat("Replayed nonce detected ({0} {1}).  Rejecting message.", message.Nonce, message.UtcCreationDate);
					throw new ReplayedMessageException(containingMessage);
				}
			}

			((IMessage)message).EnsureValidMessage();

			return message;
		}

		/// <summary>
		/// Serializes the <see cref="DataBag"/> instance to a buffer.
		/// </summary>
		/// <param name="message">The message.</param>
		/// <returns>The buffer containing the serialized data.</returns>
		protected abstract byte[] SerializeCore(T message);

		/// <summary>
		/// Deserializes the <see cref="DataBag"/> instance from a buffer.
		/// </summary>
		/// <param name="message">The message instance to initialize with data from the buffer.</param>
		/// <param name="data">The data buffer.</param>
		protected abstract void DeserializeCore(T message, byte[] data);

		/// <summary>
		/// Determines whether the signature on this instance is valid.
		/// </summary>
		/// <param name="signedData">The signed data.</param>
		/// <param name="signature">The signature.</param>
		/// <returns>
		///   <c>true</c> if the signature is valid; otherwise, <c>false</c>.
		/// </returns>
		private bool IsSignatureValid(byte[] signedData, byte[] signature) {
			Contract.Requires<ArgumentNullException>(signedData != null, "message");
			Contract.Requires<ArgumentNullException>(signature != null, "signature");

			if (this.asymmetricSigning != null) {
				return this.asymmetricSigning.VerifyData(signedData, this.hasherForAsymmetricSigning, signature);
			} else {
				return MessagingUtilities.AreEquivalentConstantTime(signature, this.CalculateSignature(signedData));
			}
		}

		/// <summary>
		/// Calculates the signature for the data in this verification code.
		/// </summary>
		/// <param name="bytesToSign">The bytes to sign.</param>
		/// <returns>
		/// The calculated signature.
		/// </returns>
		private byte[] CalculateSignature(byte[] bytesToSign) {
			Contract.Requires<ArgumentNullException>(bytesToSign != null, "bytesToSign");
			Contract.Requires<InvalidOperationException>(this.asymmetricSigning != null || this.symmetricHasher != null);
			Contract.Ensures(Contract.Result<byte[]>() != null);

			if (this.asymmetricSigning != null) {
				return this.asymmetricSigning.SignData(bytesToSign, this.hasherForAsymmetricSigning);
			} else {
				return this.symmetricHasher.ComputeHash(bytesToSign);
			}
		}

		/// <summary>
		/// Encrypts the specified value using either the symmetric or asymmetric encryption algorithm as appropriate.
		/// </summary>
		/// <param name="value">The value.</param>
		/// <returns>The encrypted value.</returns>
		private byte[] Encrypt(byte[] value) {
			Contract.Requires<InvalidOperationException>(this.asymmetricEncrypting != null || this.symmetricSecret != null);

			if (this.asymmetricEncrypting != null) {
				return this.asymmetricEncrypting.EncryptWithRandomSymmetricKey(value);
			} else {
				return MessagingUtilities.Encrypt(value, this.symmetricSecret);
			}
		}

		/// <summary>
		/// Decrypts the specified value using either the symmetric or asymmetric encryption algorithm as appropriate.
		/// </summary>
		/// <param name="value">The value.</param>
		/// <returns>The decrypted value.</returns>
		private byte[] Decrypt(byte[] value) {
			Contract.Requires<InvalidOperationException>(this.asymmetricEncrypting != null || this.symmetricSecret != null);

			if (this.asymmetricEncrypting != null) {
				return this.asymmetricEncrypting.DecryptWithRandomSymmetricKey(value);
			} else {
				return MessagingUtilities.Decrypt(value, this.symmetricSecret);
			}
		}
	}
}
