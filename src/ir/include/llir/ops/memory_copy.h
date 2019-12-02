/* Copyright 2019 Canaan Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
#pragma once
#include "../node.h"
#include <xtensor/xtensor.hpp>

namespace nncase
{
namespace llir
{
    class memory_copy : public node
    {
    public:
        DEFINE_NODE_OPCODE(op_memory_copy);

        input_connector &input() { return input_at(0); }
        output_connector &output() { return output_at(0); }

        const shape_t &new_shape() const noexcept { return new_shape_; }

        memory_copy(datatype_t input_type, shape_t input_shape, datatype_t output_type, shape_t new_shape);

    private:
        shape_t new_shape_;
    };
}
}
