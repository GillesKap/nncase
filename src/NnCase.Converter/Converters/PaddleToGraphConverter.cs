﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using NnCase.Converter.Model;
using NnCase.Converter.Model.Layers;
using paddle = Paddle.Framework.Proto;

namespace NnCase.Converter.Converters
{
    public class PaddleToGraphConverter
    {
        private readonly string _modelPath;
        private readonly paddle.ProgramDesc _programDesc;
        private readonly Dictionary<InputConnector, string> _inputs;
        private readonly Dictionary<string, OutputConnector> _outputs;

        private paddle.BlockDesc _subgraph;
        public Graph Graph { get; private set; }

        public PaddleToGraphConverter(string modelPath)
        {
            _modelPath = modelPath;
            _programDesc = paddle.ProgramDesc.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(modelPath, "__model__")));

            _inputs = new Dictionary<InputConnector, string>();
            _outputs = new Dictionary<string, OutputConnector>();
        }

        public void Convert(int subgraphIndex)
        {
            _subgraph = _programDesc.Blocks[subgraphIndex];
            var layers = _subgraph.Ops.Select(ConvertOperator).ToList();
            foreach (var inputPair in _inputs)
            {
                if (_outputs.TryGetValue(inputPair.Value, out var output))
                {
                    inputPair.Key.SetConnection(output);
                }
            }

            var inputs = new List<InputLayer>();
            foreach (var conn in _inputs.Keys.Where(o => o.Connection == null))
            {
                if (_inputs.TryGetValue(conn, out var varName))
                {
                    var vv = GetVar(varName);
                    var v = LoadVarDataOrDefault<float>(varName);
                    var c = new Constant(GetVarShape(varName), v);
                    conn.SetConnection(c.Output);
                }
                else
                {
                    var input = new InputLayer(conn.Dimensions);
                    conn.SetConnection(input.Output);
                    inputs.Add(input);
                }
            }

            var outputs = new List<OutputLayer>();
            foreach (var conn in _outputs.Values.Where(o => !o.Connections.Any()))
            {
                var output = new OutputLayer(conn.Dimensions);
                conn.AddConnection(output.Input);
                outputs.Add(output);
            }

            Graph = new Graph(inputs, outputs);
        }

        private Layer ConvertOperator(paddle.OpDesc op)
        {
            switch (op.Type)
            {
                case "feed":
                    return ConvertFeed(op);
                case "fetch":
                    return ConvertFetch(op);
                case "conv2d":
                    return ConvertConv2d(op);
                case "elementwise_add":
                    return ConvertElementwiseAdd(op);
                case "batch_norm":
                    return ConvertBatchNorm(op);
                case "relu":
                    return ConvertRelu(op);
                case "pool2d":
                    return ConvertPool2d(op);
                case "reshape":
                    return ConvertReshape(op);
                case "softmax":
                    return ConvertSoftmax(op);
                default:
                    throw new NotSupportedException();
            }
        }

        private Layer ConvertFeed(paddle.OpDesc op)
        {
            var output = op.Outputs[0].Arguments[0];
            var layer = new InputLayer(GetVarShape(output));
            _outputs.Add(output, layer.Output);
            return layer;
        }

        private Layer ConvertConv2d(paddle.OpDesc op)
        {
            var padding = GetAttr(op, "paddings").Ints;
            var strides = GetAttr(op, "strides").Ints.ToArray();
            var groups = GetAttr(op, "groups").I;
            if (strides[0] == 0) strides[0] = 1;
            if (strides[1] == 0) strides[1] = 1;

            var input = op.Inputs[1].Arguments[0];
            var weights = op.Inputs[0].Arguments[0];
            var weightsShape = GetVarShape(weights);
            var kernelWidth = weightsShape[3];
            var kernelHeight = weightsShape[2];
            var output = op.Outputs[0].Arguments[0];

            if (groups == 1)
            {
                Conv2d conv2d;
                if (padding[0] == 1 && padding[1] == 1 && strides[0] == 2 && strides[1] == 2 &&
                    kernelWidth == 3 && kernelHeight == 3)
                {
                    var space = new SpaceToBatchNd(GetVarShape(input), new[] { 1, 1 }.ToTensor(), new[,] { { 1, 1 }, { 1, 1, } }.ToTensor());
                    conv2d = new Conv2d(space.Output.Dimensions, LoadVarData<float>(weights), null, Padding.Valid, strides[1], strides[0], ActivationFunctionType.Linear);
                    conv2d.Input.SetConnection(space.Output);
                    _inputs.Add(space.Input, input);
                }
                else
                {
                    if (padding[0] != padding[1] || (padding[0] != 0 && padding[0] != 1))
                        throw new NotSupportedException();

                    conv2d = new Conv2d(GetVarShape(input), LoadVarData<float>(weights), null, Padding.Same, strides[1], strides[0], ActivationFunctionType.Linear);
                    _inputs.Add(conv2d.Input, input);
                }

                _outputs.Add(output, conv2d.Output);
                return conv2d;
            }
            else if (groups == weightsShape[0])
            {
                var w = LoadVarData<float>(weights).Transpose(new[] { 1, 0, 2, 3 });
                DepthwiseConv2d dwConv2d;
                if (padding[0] == 1 && padding[1] == 1 && strides[0] == 2 && strides[1] == 2 &&
                    kernelWidth == 3 && kernelHeight == 3)
                {
                    var space = new SpaceToBatchNd(GetVarShape(input), new[] { 1, 1 }.ToTensor(), new[,] { { 1, 1 }, { 1, 1, } }.ToTensor());
                    dwConv2d = new DepthwiseConv2d(space.Output.Dimensions, w, null, Padding.Valid, strides[1], strides[0], ActivationFunctionType.Linear);
                    dwConv2d.Input.SetConnection(space.Output);
                    _inputs.Add(space.Input, input);
                }
                else
                {
                    if (padding[0] != padding[1] || (padding[0] != 0 && padding[0] != 1))
                        throw new NotSupportedException();

                    dwConv2d = new DepthwiseConv2d(GetVarShape(input), w, null, Padding.Same, strides[1], strides[0], ActivationFunctionType.Linear);
                    _inputs.Add(dwConv2d.Input, input);
                }

                _outputs.Add(output, dwConv2d.Output);
                return dwConv2d;
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private Layer ConvertElementwiseAdd(paddle.OpDesc op)
        {
            var x = op.Inputs[0].Arguments[0];
            var y = op.Inputs[1].Arguments[0];
            var output = op.Outputs[0].Arguments[0];

            var layer = new Add(GetVarShape(x), GetVarShape(y));
            _inputs.Add(layer.InputA, x);
            _inputs.Add(layer.InputB, y);
            _outputs.Add(output, layer.Output);
            return layer;
        }

        private Layer ConvertBatchNorm(paddle.OpDesc op)
        {
            var epsilon = GetAttr(op, "epsilon").F;
            var offset = op.Inputs[0].Arguments[0];
            var mean = op.Inputs[1].Arguments[0];
            var scale = op.Inputs[2].Arguments[0];
            var variance = op.Inputs[3].Arguments[0];
            var x = op.Inputs[4].Arguments[0];
            var output = op.Outputs[4].Arguments[0];

            var layer = new BatchNormalization(GetVarShape(x), LoadVarData<float>(scale), LoadVarData<float>(offset),
                LoadVarData<float>(mean), LoadVarData<float>(variance), epsilon);
            _inputs.Add(layer.Input, x);
            _outputs.Add(output, layer.Output);
            return layer;
        }

        private Layer ConvertRelu(paddle.OpDesc op)
        {
            var x = op.Inputs[0].Arguments[0];
            var output = op.Outputs[0].Arguments[0];

            var layer = new Relu(GetVarShape(x));
            layer.Input.SetConnection(_outputs[x]);
            _outputs[output] = layer.Output;
            return layer;
        }

        private Layer ConvertPool2d(paddle.OpDesc op)
        {
            var type = GetAttr(op, "pooling_type").S;
            var ksize = GetAttr(op, "ksize").Ints;
            var strides = GetAttr(op, "strides").Ints;
            var x = op.Inputs[0].Arguments[0];
            var output = op.Outputs[0].Arguments[0];

            if (type == "avg")
            {
                var layer = new AveragePool2d(GetVarShape(x), Padding.Valid, ksize[1], ksize[0], strides[1], strides[0], ActivationFunctionType.Linear);
                _inputs.Add(layer.Input, x);
                _outputs.Add(output, layer.Output);
                return layer;
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private Layer ConvertReshape(paddle.OpDesc op)
        {
            var shape = GetAttr(op, "shape").Ints;
            var x = op.Inputs[1].Arguments[0];
            var output = op.Outputs[0].Arguments[0];

            var layer = new Reshape(GetVarShape(x), shape.ToArray());
            _inputs.Add(layer.Input, x);
            _outputs.Add(output, layer.Output);
            return layer;
        }

        private Layer ConvertSoftmax(paddle.OpDesc op)
        {
            var x = op.Inputs[0].Arguments[0];
            var output = op.Outputs[0].Arguments[0];

            var layer = new Softmax(GetVarShape(x));
            _inputs.Add(layer.Input, x);
            _outputs.Add(output, layer.Output);
            return layer;
        }

        private Layer ConvertFetch(paddle.OpDesc op)
        {
            var input = op.Inputs[0].Arguments[0];
            var layer = new OutputLayer(GetVarShape(input));
            return layer;
        }

        private paddle.VarDesc GetVar(string name)
        {
            return _subgraph.Vars.First(o => o.Name == name);
        }

        private int[] GetVarShape(string name)
        {
            var v = GetVar(name);
            return v.Type.LodTensor.Tensor.Dims.Select(x => (int)x).ToArray();
        }

        private paddle.OpDesc.Types.Attr GetAttr(paddle.OpDesc op, string name)
        {
            return op.Attrs.First(x => x.Name == name);
        }

        private Tensor<T> LoadVarData<T>(string name)
            where T : unmanaged
        {
            var v = GetVar(name);
            var reader = new SpanReader(File.ReadAllBytes(Path.Combine(_modelPath, name)));

            var version = reader.Read<uint>();
            var lodLevel = reader.Read<ulong>();
            for (uint i = 0; i < lodLevel; i++)
            {
                var len = reader.Read<ulong>();
                reader.Skip((int)len);
            }

            version = reader.Read<uint>();
            if (version != 0)
                throw new NotSupportedException();
            {
                var descSize = reader.Read<int>();
                reader.Skip(descSize);
            }

            var rest = reader.ReadAsSpan();
            var data = MemoryMarshal.Cast<byte, T>(rest);

            return new DenseTensor<T>(data.ToArray(), GetVarShape(name));
        }

        private Tensor<T> LoadVarDataOrDefault<T>(string name)
            where T : unmanaged
        {
            if (File.Exists(Path.Combine(_modelPath, name)))
            {
                return LoadVarData<T>(name);
            }
            else
            {
                var v = GetVar(name);
                return new DenseTensor<T>(v.Type.LodTensor.Tensor.Dims.Select(x => (int)x).ToArray());
            }
        }
    }
}