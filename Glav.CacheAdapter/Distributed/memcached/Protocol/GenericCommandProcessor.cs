﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;
using Glav.CacheAdapter.Core.Diagnostics;

namespace Glav.CacheAdapter.Distributed.memcached.Protocol
{
	public class GenericCommandProcessor
	{
		private SupportedCommands _command;
		private string _ipAdress;
		private int _port;
		private CommandMapper _mapper = new CommandMapper();
		private CommandSocket _cmdSocket;
		private string _commandToExecute = null;
		public event EventHandler<CommunicationFailureEventArgs> CommunicationFailure;
		private ILogging _logger;

		public GenericCommandProcessor(ILogging logger, SupportedCommands command, string ipAdress, int port)
		{
			_command = command;
			_ipAdress = ipAdress;
			_port = port;
			_cmdSocket = new CommandSocket(_ipAdress,_port);
			_cmdSocket.CommunicationFailure += new EventHandler<CommunicationFailureEventArgs>(_cmdSocket_CommunicationFailure);
			_logger = logger;
		}

		protected ILogging Logger { get { return _logger; } }

		protected CommandSocket ProtocolSocket
		{
			get { return _cmdSocket; }
		}
		
		public byte[] SerialiseData(object data)
		{
			_logger.WriteInfoMessage("Serialising data");

			#region Was used for Bianry seriaisation
			// This code was originally in place for binary serialisation however this requires that
			// all objects to be serialised are marked with [Serializable] attribute which is 
			// cumbersom. So have changed to using the slightly less efficient but far easier to use
			// NetDataContractSerializer. Cache engines like AppFabric also use this.
			//using (var memStream = new MemoryStream())
			//{
			//    new BinaryFormatter().Serialize(memStream, data);
			//    memStream.Seek(0, SeekOrigin.Begin);
			//    var serialisedData = new byte[memStream.Length];
			//    memStream.Read(serialisedData, 0, (int)memStream.Length);
			//    return serialisedData;
			//}
			#endregion

			using (var memStream = new MemoryStream())
			{
				NetDataContractSerializer ser = new NetDataContractSerializer();
				ser.Serialize(memStream, data);
				memStream.Seek(0, SeekOrigin.Begin);
				var serialisedData = new byte[memStream.Length];
				memStream.Read(serialisedData, 0, (int)memStream.Length);
				return serialisedData;
			}

		}

		public object DeserialiseData(byte[] data)
		{
			_logger.WriteInfoMessage("Deserialising data");

			#region Was used for Binary serialisation
			// This code was originally in place for binary serialisation however this requires that
			// all objects to be serialised are marked with [Serializable] attribute which is 
			// cumbersom. So have changed to using the slightly less efficient but far easier to use
			// NetDataContractSerializer. Cache engines like AppFabric also use this.
			//using (var memStream = new MemoryStream(data))
			//{
			//    var deserialisedData = new BinaryFormatter().Deserialize(memStream);
			//    return deserialisedData;
			//}
			#endregion

			using (var memStream = new MemoryStream(data))
			{
				var deserialisedData = new NetDataContractSerializer().Deserialize(memStream);
				return deserialisedData;
			}
		}

		protected byte[] SetCommandParameters(params string[] args)
		{
			_logger.WriteInfoMessage("Setting command parameters");
			if (args != null && args.Length > 0)
			{
				_commandToExecute = string.Format(_mapper.GetCommandFormat(_command), args);
			}
			else
			{
				_commandToExecute = _mapper.GetCommandFormat(_command);
			}
			_commandToExecute += ServerProtocol.Command_Terminator;
			
			_logger.WriteInfoMessage(string.Format("Cmd To Execute: [{0}]", _commandToExecute));

			return UTF8Encoding.ASCII.GetBytes(_commandToExecute);
		}

		public virtual CommandResponse ExecuteCommand()
		{
			if (string.IsNullOrWhiteSpace(_commandToExecute))
			{
				SetCommandParameters();
			}

			var response = new CommandResponse();
			try
			{
				var result = _cmdSocket.Send(_commandToExecute);
				response = ProcessResponse(result);
			} catch (Exception ex)
			{
				//todo: should log ex.Message somewhere
				_logger.WriteException(ex);
				response.Status = CommandResponseStatus.Error;
			}
			return response;
		}

		protected virtual CommandResponse ProcessResponse(byte[] rawResponse)
		{
			_logger.WriteInfoMessage("Processing Response");

			var response = DetermineIfAnyProtocolErrorsOccurred(rawResponse);

			response.RawData = rawResponse;
			if (response.Status == CommandResponseStatus.Ok)
			{
				response.ResponseText = UTF8Encoding.ASCII.GetString(rawResponse);
			}

			_logger.WriteInfoMessage(string.Format("Response Text :[{0}], Response Status: [{1}]", response.ResponseText, response.Status));
			
			return response;
		}

		private CommandResponse DetermineIfAnyProtocolErrorsOccurred(byte[] rawResponse)
		{
			var response = new CommandResponse();
			if (rawResponse == null)
			{
				response.Status = CommandResponseStatus.Error;
				return response;
			}

			if (CheckForByteSequenceInArray(rawResponse,ServerProtocol.ServerSuccessEndResponse + ServerProtocol.Command_Terminator))
			{
				response.Status = CommandResponseStatus.Ok;
				return response;
			}

			if (CheckForByteSequenceInArray(rawResponse, ServerProtocol.GenericErrorResponse + ServerProtocol.Command_Terminator))
			{
				response.Status = CommandResponseStatus.Error;
				return response;
			}

			if (CheckForByteSequenceInArray(rawResponse, ServerProtocol.ClientErrorResponse))
			{
				response.Status = CommandResponseStatus.ClientError;
				return response;
			}

			if (CheckForByteSequenceInArray(rawResponse, ServerProtocol.ServerErrorResponse))
			{
				response.Status = CommandResponseStatus.ServerError;
				return response;
			}

			response.Status = CommandResponseStatus.Ok;
			return response;
		}

		protected bool CheckForByteSequenceInArray(byte[] arrayToCheck, string responseToCheckFor)
		{
			// Convert the text we want to check for into a byte array
			var responseBytesToCheckFor =
				UTF8Encoding.ASCII.GetBytes(responseToCheckFor);

			// If the source array to check is long enough...
			if (arrayToCheck.Length >= responseBytesToCheckFor.Length)
			{
				// Create a temp array to copy only the necessary bytes into from the array to check so we end up with
				// 2 arrays of identical length, the temp one being just a partial copy of the array to check
				var tmpCheckArray = new byte[responseBytesToCheckFor.Length];
				// Copy the bytes from the source array into the temp array
				Array.Copy(arrayToCheck, tmpCheckArray, tmpCheckArray.Length);
				// Finally do the check
				if (tmpCheckArray.SequenceEqual(responseBytesToCheckFor))
				{
					return true;
				}
			}

			return false;
		}
		
		void _cmdSocket_CommunicationFailure(object sender, CommunicationFailureEventArgs e)
		{
			FireCommsFailureBubbledEvent(sender,e);
		}

		public void FireCommsFailureBubbledEvent(object sender, CommunicationFailureEventArgs e)
		{
			if (CommunicationFailure != null)
			{
				CommunicationFailure(sender, e);
			}
		}
	}

}
