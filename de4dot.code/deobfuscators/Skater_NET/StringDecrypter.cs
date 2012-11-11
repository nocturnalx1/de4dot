﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using dot10.DotNet;
using dot10.DotNet.Emit;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.Skater_NET {
	class StringDecrypter {
		ModuleDefMD module;
		TypeDef decrypterType;
		MethodDef decrypterCctor;
		FieldDefinitionAndDeclaringTypeDict<string> fieldToDecryptedString = new FieldDefinitionAndDeclaringTypeDict<string>();
		bool canRemoveType;
		IDecrypter decrypter;

		interface IDecrypter {
			string decrypt(string encrypted);
		}

		class DecrypterV1 : IDecrypter {
			byte[] key;
			byte[] iv;

			public DecrypterV1(byte[] key, byte[] iv) {
				this.key = key;
				this.iv = iv;
			}

			public string decrypt(string encrypted) {
				try {
					return Encoding.Unicode.GetString(DeobUtils.des3Decrypt(Convert.FromBase64String(encrypted), key, iv));
				}
				catch (FormatException) {
					return "";
				}
			}
		}

		class DecrypterV2 : IDecrypter {
			public string decrypt(string encrypted) {
				var ints = encrypted.Split(' ');
				if (ints.Length % 3 != 0)
					throw new ApplicationException("Invalid encrypted string");
				var sb = new StringBuilder(ints.Length / 3);
				for (int i = 0; i < ints.Length; i += 3) {
					int val1 = int.Parse(ints[i]);
					int val2 = int.Parse(ints[i + 1]);
					if ((double)val2 / 2.0 == Math.Round((double)val2 / 2.0))
						val1 += val1;
					sb.Append((char)val1);
				}
				return sb.ToString();
			}
		}

		public bool Detected {
			get { return decrypterType != null; }
		}

		public bool CanRemoveType {
			get { return canRemoveType; }
		}

		public TypeDef Type {
			get { return decrypterType; }
		}

		public StringDecrypter(ModuleDefMD module) {
			this.module = module;
		}

		public void find() {
			foreach (var type in module.Types) {
				if (type.HasProperties || type.HasEvents)
					continue;

				var cctor = type.FindClassConstructor();
				if (cctor == null)
					continue;

				if (checkType(type)) {
					canRemoveType = true;
					decrypterType = type;
					decrypterCctor = cctor;
					return;
				}
			}
		}

		public void initialize(ISimpleDeobfuscator deobfuscator) {
			if (decrypterCctor == null)
				return;

			deobfuscator.deobfuscate(decrypterCctor);
			var instrs = decrypterCctor.Body.Instructions;
			for (int i = 0; i < instrs.Count - 4; i++) {
				var ldstr = instrs[i];
				if (ldstr.OpCode.Code != Code.Ldstr)
					continue;
				var encryptedString = ldstr.Operand as string;
				if (encryptedString == null)
					continue;
				if (instrs[i + 1].OpCode.Code != Code.Stsfld)
					continue;
				if (instrs[i + 2].OpCode.Code != Code.Ldsfld)
					continue;
				if (instrs[i + 3].OpCode.Code != Code.Call)
					continue;
				if (instrs[i + 4].OpCode.Code != Code.Stsfld)
					continue;
				var field = instrs[i + 4].Operand as FieldDef;
				if (field == null)
					continue;
				if (!new SigComparer().Equals(field.DeclaringType, decrypterType))
					continue;

				fieldToDecryptedString.add(field, decrypter.decrypt(encryptedString));
			}
		}

		bool checkType(TypeDef type) {
			foreach (var method in type.Methods) {
				if (!method.IsStatic || method.Body == null)
					continue;
				if (!DotNetUtils.isMethod(method, "System.String", "(System.String)"))
					continue;

				if (checkMethodV1(method))
					return true;
				if (checkMethodV2(method))
					return true;
			}

			return false;
		}

		bool checkMethodV1(MethodDef method) {
			var salt = getSalt(method);
			if (salt == null)
				return false;

			var password = getPassword(method);
			if (string.IsNullOrEmpty(password))
				return false;

			var passwordBytes = new PasswordDeriveBytes(password, salt);
			var key = passwordBytes.GetBytes(16);
			var iv = passwordBytes.GetBytes(8);
			decrypter = new DecrypterV1(key, iv);
			return true;
		}

		static string[] callsMethodsV2 = new string[] {
			"System.String[] System.String::Split(System.Char[])",
			"System.Int32 System.Array::GetUpperBound(System.Int32)",
			"System.String Microsoft.VisualBasic.CompilerServices.Conversions::ToString(System.Char)",
			"System.Int32 Microsoft.VisualBasic.CompilerServices.Conversions::ToInteger(System.String)",
			"System.String System.String::Concat(System.String,System.String)",
			"System.Char Microsoft.VisualBasic.Strings::Chr(System.Int32)",
		};
		bool checkMethodV2(MethodDef method) {
			if (!DeobUtils.hasInteger(method, ' '))
				return false;
			foreach (var calledMethodName in callsMethodsV2) {
				if (!DotNetUtils.callsMethod(method, calledMethodName))
					return false;
			}

			decrypter = new DecrypterV2();
			return true;
		}

		static byte[] getSalt(MethodDef method) {
			foreach (var s in DotNetUtils.getCodeStrings(method)) {
				var saltAry = fixSalt(s);
				if (saltAry != null)
					return saltAry;
			}

			return null;
		}

		static byte[] fixSalt(string s) {
			if (s.Length < 10 || s.Length > 30 || s.Length / 2 * 2 != s.Length)
				return null;

			var ary = s.ToCharArray();
			Array.Reverse(ary);
			for (int i = 0; i < ary.Length; i++)
				ary[i]--;
			var s2 = new string(ary);

			var saltAry = new byte[(int)Math.Round((double)s2.Length / 2 - 1) + 1];
			for (int i = 0; i < saltAry.Length; i++) {
				int result;
				if (!int.TryParse(s2.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, null, out result))
					return null;
				saltAry[i] = (byte)result;
			}

			return saltAry;
		}

		string getPassword(MethodDef decryptMethod) {
			foreach (var method in DotNetUtils.getCalledMethods(module, decryptMethod)) {
				if (!method.IsStatic || method.Body == null)
					continue;
				if (!new SigComparer().Equals(method.DeclaringType, decryptMethod.DeclaringType))
					continue;
				if (!DotNetUtils.isMethod(method, "System.String", "()"))
					continue;

				var hexChars = getPassword2(method);
				if (string.IsNullOrEmpty(hexChars))
					continue;

				var password = fixPassword(hexChars);
				if (string.IsNullOrEmpty(password))
					continue;

				return password;
			}
			return null;
		}

		string fixPassword(string hexChars) {
			var ary = hexChars.Trim().Split(' ');
			string password = "";
			for (int i = 0; i < ary.Length; i++) {
				int result;
				if (!int.TryParse(ary[i], NumberStyles.AllowHexSpecifier, null, out result))
					return null;
				password += (char)result;
			}
			return password;
		}

		string getPassword2(MethodDef method) {
			string password = "";
			foreach (var calledMethod in DotNetUtils.getCalledMethods(module, method)) {
				var s = getPassword3(calledMethod);
				if (string.IsNullOrEmpty(s))
					return null;

				password += s;
			}
			return password;
		}

		string getPassword3(MethodDef method) {
			var strings = new List<string>(DotNetUtils.getCodeStrings(method));
			if (strings.Count != 1)
				return null;

			var s = strings[0];
			if (!Regex.IsMatch(s, @"^[a-fA-F0-9]{2} $"))
				return null;

			return s;
		}

		public void deobfuscate(Blocks blocks) {
			foreach (var block in blocks.MethodBlocks.getAllBlocks()) {
				var instrs = block.Instructions;
				for (int i = 0; i < instrs.Count; i++) {
					var instr = instrs[i];

					if (instr.OpCode.Code == Code.Call || instr.OpCode.Code == Code.Callvirt) {
						if (blocks.Method.DeclaringType == decrypterType)
							continue;
						var calledMethod = instr.Operand as IMethod;
						if (calledMethod != null && calledMethod.DeclaringType == decrypterType)
							canRemoveType = false;
					}
					else if (instr.OpCode.Code == Code.Ldsfld) {
						if (instr.OpCode.Code != Code.Ldsfld)
							continue;
						var field = instr.Operand as IField;
						if (field == null)
							continue;
						var decrypted = fieldToDecryptedString.find(field);
						if (decrypted == null)
							continue;

						instrs[i] = new Instr(Instruction.Create(OpCodes.Ldstr, decrypted));
						Logger.v("Decrypted string: {0}", Utils.toCsharpString(decrypted));
					}
				}
			}
		}
	}
}
